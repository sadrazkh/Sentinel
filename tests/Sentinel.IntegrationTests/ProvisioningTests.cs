using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The provisioning saga end to end.
/// <para>
/// The behaviour worth the most scrutiny is what happens when a panel write ends without an answer:
/// it must never be retried, because a repeated create makes a second client and a repeated delete
/// makes a confusing error. Several tests here exist purely to prove that.
/// </para>
/// </summary>
public sealed class ProvisioningTests : IClassFixture<VpnTestFactory>, IAsyncLifetime
{
    private const long FiftyGibibytes = 53_687_091_200L;
    private const string PanelToken = "integration-only-panel-token-24680";

    /// <summary>Every server in this suite shares one panel address; none of these tests moves a
    /// service between panels, which is what <c>MigrationTests</c> is for.</summary>
    private const string PanelUrl = "https://panel.example.com:2053";

    private readonly VpnTestFactory _factory;

    public ProvisioningTests(VpnTestFactory factory) => _factory = factory;

    /// <summary>
    /// Leaves nothing of the previous test in flight.
    /// <para>
    /// The class shares one host and one database, and several tests here deliberately end with a
    /// service parked or a job queued. A sweep in the next test would pick that leftover up too, so
    /// every count assertion would silently depend on the order xUnit happened to choose. Draining
    /// first means a test's own sweep returns its own work and nothing else.
    /// </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        _factory.Panel.AllCallsUnknown = false;
        _factory.Panel.ClearScripts();

        // Converges: reconciling a parked job queues at most one replacement, and a job that has
        // failed its way into a backoff is not runnable, so each pass has strictly less to do.
        for (var pass = 0; pass < 10; pass++)
        {
            if (await RunJobsAsync() + await ReconcileAsync() == 0)
            {
                break;
            }
        }

        // Drained work may have left scripted outcomes unconsumed.
        _factory.Panel.ClearScripts();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------------- fixtures ----

    private Task<Guid> CreateServerAsync(string key, string country = "DE", int maxClients = 10) =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var created = await admin.SaveAsync(null, new VpnServerSaveRequest(
                key, $"سرور {key}", $"Server {key}", country,
                PanelUrl, PanelToken,
                VpnServerStatus.Active, maxClients, 100, null, null));

            Assert.True(created.Succeeded, created.ErrorKey);

            var serverId = created.Value;

            // Promote to Active and allowlist an inbound directly: the admin path would contact the
            // panel, and this fixture is about what happens *after* a server is ready.
            var db = services.GetRequiredService<IVpnDbContext>();

            var server = await db.VpnServers.FirstAsync(candidate => candidate.Id == serverId);
            server.Status = VpnServerStatus.Active;
            server.Health = VpnServerHealth.Healthy;

            db.ServerInboundProfiles.Add(new ServerInboundProfile
            {
                Id = Guid.NewGuid(),
                ServerId = serverId,
                InboundId = 1,
                Label = "vless:443",
                Protocol = "vless",
                IsEnabled = true,
            });

            await db.SaveChangesAsync();

            return serverId;
        });

    private Task<Guid> CreatePlanAsync(Guid productId, string key, string? country = null, int days = 30) =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                key, productId, $"پلن {key}", $"Plan {key}", null, null,
                FiftyGibibytes, days, 2, 1_000_000, "IRR",
                true, true, country, 100, false, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    private Task<int> RunJobsAsync(int batch = 20) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IProvisioningExecutor>().RunPendingAsync(batch));

    private Task<int> ReconcileAsync(int batch = 20) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IReconciliationService>().ReconcileAsync(batch));

    private Task<int> SyncUsageAsync(int batch = 20) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IReconciliationService>().SyncUsageAsync(batch));

    private Task<CustomerService> LoadServiceAsync(Guid serviceId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .FirstAsync(service => service.Id == serviceId));

    private async Task<Guid> ProvisionedServiceAsync(string prefix)
    {
        var userId = await _factory.CreateMemberAsync($"{prefix}-member");
        var productId = await _factory.CreateProductAsync($"{prefix}-product");
        await CreateServerAsync($"{prefix}-server");
        var planId = await CreatePlanAsync(productId, $"{prefix}-plan");

        var serviceId = await _factory.WithScopeAsync(async services =>
        {
            var manager = services.GetRequiredService<ICustomerServiceManager>();
            var result = await manager.CreateAsync(new CreateServiceRequest(userId, planId, null));

            Assert.True(result.Succeeded, result.ErrorKey);
            return result.Value;
        });

        await RunJobsAsync();

        return serviceId;
    }

    // ------------------------------------------------------------------------- happy path ----

    [Fact]
    public async Task Creating_a_service_queues_it_and_the_worker_provisions_it()
    {
        var serviceId = await ProvisionedServiceAsync("prov-happy");

        var service = await LoadServiceAsync(serviceId);

        Assert.Equal(CustomerServiceStatus.Active, service.Status);
        Assert.NotNull(service.PanelClientEmail);
        Assert.True(_factory.Panel.Clients.ContainsKey(service.PanelClientEmail!));
    }

    [Fact]
    public async Task The_terms_come_from_the_plan_and_not_from_the_request()
    {
        var serviceId = await ProvisionedServiceAsync("prov-terms");
        var service = await LoadServiceAsync(serviceId);

        Assert.Equal(FiftyGibibytes, service.TrafficBytes);
        Assert.Equal(2, service.DeviceLimit);

        // And the same values reached the panel, in the panel's own units.
        var client = _factory.Panel.Clients[service.PanelClientEmail!];

        Assert.Equal(FiftyGibibytes, client.TotalAllowanceBytes);
        Assert.Equal(2, client.IpLimit);
    }

    [Fact]
    public async Task The_panel_identifier_is_opaque_and_not_the_members_address()
    {
        var serviceId = await ProvisionedServiceAsync("prov-opaque");
        var service = await LoadServiceAsync(serviceId);

        Assert.True(PanelClientEmail.IsValid(service.PanelClientEmail));
        Assert.DoesNotContain("@", service.PanelClientEmail!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            serviceId.ToString("N"), service.PanelClientEmail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capacity_is_reserved_when_the_service_is_created()
    {
        var userId = await _factory.CreateMemberAsync("prov-capacity-member");
        var productId = await _factory.CreateProductAsync("prov-capacity-product");
        var serverId = await CreateServerAsync("prov-capacity-server", maxClients: 3);
        var planId = await CreatePlanAsync(productId, "prov-capacity-plan");

        await _factory.WithScopeAsync(async services =>
        {
            var manager = services.GetRequiredService<ICustomerServiceManager>();
            var db = services.GetRequiredService<IVpnDbContext>();

            var before = (await db.VpnServers.AsNoTracking()
                .FirstAsync(server => server.Id == serverId)).ReservedClients;

            await manager.CreateAsync(new CreateServiceRequest(userId, planId, null));

            var after = (await db.VpnServers.AsNoTracking()
                .FirstAsync(server => server.Id == serverId)).ReservedClients;

            Assert.Equal(before + 1, after);
        });
    }

    [Fact]
    public async Task A_full_server_refuses_a_new_service_up_front()
    {
        // The refusal happens at creation, not at provisioning time — a member should be told
        // immediately rather than watching a service sit in Pending.
        var productId = await _factory.CreateProductAsync("prov-full-product");
        await CreateServerAsync("prov-full-server", country: "FR", maxClients: 1);
        var planId = await CreatePlanAsync(productId, "prov-full-plan", country: "FR");

        var first = await _factory.CreateMemberAsync("prov-full-first");
        var second = await _factory.CreateMemberAsync("prov-full-second");

        await _factory.WithScopeAsync(async services =>
        {
            var manager = services.GetRequiredService<ICustomerServiceManager>();

            Assert.True((await manager.CreateAsync(new CreateServiceRequest(first, planId, null))).Succeeded);

            var overflow = await manager.CreateAsync(new CreateServiceRequest(second, planId, null));

            Assert.False(overflow.Succeeded);
            Assert.Equal(ServiceErrors.NoCapacity, overflow.ErrorKey);
        });
    }

    // ------------------------------------------------------------------- unknown outcomes ----

    [Fact]
    public async Task A_create_with_an_unknown_outcome_is_never_retried()
    {
        // The central rule. A retry could produce a second client on the panel — a customer with two
        // configurations and their quota counted twice.
        var userId = await _factory.CreateMemberAsync("prov-unknown-member");
        var productId = await _factory.CreateProductAsync("prov-unknown-product");
        await CreateServerAsync("prov-unknown-server");
        var planId = await CreatePlanAsync(productId, "prov-unknown-plan");

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);

        var serviceId = await _factory.WithScopeAsync(async services =>
        {
            var manager = services.GetRequiredService<ICustomerServiceManager>();
            var result = await manager.CreateAsync(new CreateServiceRequest(userId, planId, null));
            return result.Value;
        });

        await RunJobsAsync();

        var service = await LoadServiceAsync(serviceId);
        Assert.Equal(CustomerServiceStatus.NeedsAttention, service.Status);

        var createCallsAfterFirstRun = _factory.Panel.Calls.Count(call => call.StartsWith("create", StringComparison.Ordinal));

        // Running the worker again must not touch the panel for this service.
        await RunJobsAsync();
        await RunJobsAsync();

        Assert.Equal(
            createCallsAfterFirstRun,
            _factory.Panel.Calls.Count(call => call.StartsWith("create", StringComparison.Ordinal)));

        var job = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .ProvisioningJobs.AsNoTracking()
                .Where(candidate => candidate.ServiceId == serviceId)
                .OrderBy(candidate => candidate.CreatedAt)
                .FirstAsync());

        Assert.Equal(ProvisioningJobStatus.NeedsReconciliation, job.Status);
    }

    [Fact]
    public async Task Reconciliation_adopts_a_client_that_the_lost_write_had_actually_created()
    {
        // The write landed; only the answer was lost. Adopting rather than re-creating is exactly
        // what stops the duplicate.
        var userId = await _factory.CreateMemberAsync("prov-adopt-member");
        var productId = await _factory.CreateProductAsync("prov-adopt-product");
        await CreateServerAsync("prov-adopt-server");
        var planId = await CreatePlanAsync(productId, "prov-adopt-plan");

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);

        var serviceId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null))).Value);

        await RunJobsAsync();

        var parked = await LoadServiceAsync(serviceId);
        Assert.Equal(CustomerServiceStatus.NeedsAttention, parked.Status);

        // Simulate the truth: the client is on the panel after all.
        _factory.Panel.PlantClient(PanelUrl, parked.PanelClientEmail!, [1]);

        Assert.Equal(1, await ReconcileAsync());

        var reconciled = await LoadServiceAsync(serviceId);

        Assert.Equal(CustomerServiceStatus.Active, reconciled.Status);
        Assert.Null(reconciled.LastError);

        // One client on the panel, not two.
        Assert.Single(_factory.Panel.Clients, entry => entry.Key == parked.PanelClientEmail);
    }

    [Fact]
    public async Task Reconciliation_reprovisions_when_the_lost_write_had_not_landed()
    {
        var userId = await _factory.CreateMemberAsync("prov-redo-member");
        var productId = await _factory.CreateProductAsync("prov-redo-product");
        await CreateServerAsync("prov-redo-server");
        var planId = await CreatePlanAsync(productId, "prov-redo-plan");

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);

        var serviceId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null))).Value);

        await RunJobsAsync();
        Assert.Equal(CustomerServiceStatus.NeedsAttention, (await LoadServiceAsync(serviceId)).Status);

        // The panel does not have it, so re-creating is safe — and now provably not a duplicate.
        Assert.Equal(1, await ReconcileAsync());
        Assert.Equal(CustomerServiceStatus.Pending, (await LoadServiceAsync(serviceId)).Status);

        await RunJobsAsync();

        var finished = await LoadServiceAsync(serviceId);

        Assert.Equal(CustomerServiceStatus.Active, finished.Status);
        Assert.True(_factory.Panel.Clients.ContainsKey(finished.PanelClientEmail!));
    }

    [Fact]
    public async Task A_lost_delete_that_actually_landed_is_confirmed_rather_than_repeated()
    {
        var serviceId = await ProvisionedServiceAsync("prov-deleted");
        var service = await LoadServiceAsync(serviceId);

        // The delete works on the panel but the answer is lost.
        _factory.Panel.ScriptOnce("delete", PanelOutcome.UnknownOutcome);

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().DecommissionAsync(serviceId));

        await RunJobsAsync();

        Assert.Equal(CustomerServiceStatus.NeedsAttention, (await LoadServiceAsync(serviceId)).Status);

        // The truth: it is gone.
        _factory.Panel.RemoveClient(PanelUrl, service.PanelClientEmail!);

        Assert.Equal(1, await ReconcileAsync());

        Assert.Equal(CustomerServiceStatus.Ended, (await LoadServiceAsync(serviceId)).Status);
    }

    [Fact]
    public async Task A_refusal_the_panel_stated_is_retried_because_it_is_certain()
    {
        // The opposite case. success:false means the panel processed and declined, so nothing was
        // half-applied and a retry is safe.
        var userId = await _factory.CreateMemberAsync("prov-refused-member");
        var productId = await _factory.CreateProductAsync("prov-refused-product");
        await CreateServerAsync("prov-refused-server");
        var planId = await CreatePlanAsync(productId, "prov-refused-plan");

        _factory.Panel.ScriptOnce("create", PanelOutcome.Rejected);

        var serviceId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null))).Value);

        await RunJobsAsync();

        var job = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .ProvisioningJobs.AsNoTracking()
                .FirstAsync(candidate => candidate.ServiceId == serviceId));

        // Failed, not parked: it will be picked up again after its backoff.
        Assert.Equal(ProvisioningJobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.True(job.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    // --------------------------------------------------------------------------- lifecycle ----

    [Fact]
    public async Task Suspending_disables_the_client_without_deleting_it()
    {
        var serviceId = await ProvisionedServiceAsync("prov-suspend");
        var service = await LoadServiceAsync(serviceId);

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().SuspendAsync(serviceId));

        await RunJobsAsync();

        Assert.Equal(CustomerServiceStatus.Suspended, (await LoadServiceAsync(serviceId)).Status);

        // Still on the panel — a suspension is reversible, a deletion is not.
        Assert.True(_factory.Panel.Clients.ContainsKey(service.PanelClientEmail!));
        Assert.False(_factory.Panel.Clients[service.PanelClientEmail!].Enabled);
    }

    [Fact]
    public async Task Resuming_re_enables_it()
    {
        var serviceId = await ProvisionedServiceAsync("prov-resume");
        var service = await LoadServiceAsync(serviceId);

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().SuspendAsync(serviceId));
        await RunJobsAsync();

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().ResumeAsync(serviceId));
        await RunJobsAsync();

        Assert.Equal(CustomerServiceStatus.Active, (await LoadServiceAsync(serviceId)).Status);
        Assert.True(_factory.Panel.Clients[service.PanelClientEmail!].Enabled);
    }

    [Fact]
    public async Task A_renewal_extends_from_the_current_expiry_not_from_today()
    {
        var serviceId = await ProvisionedServiceAsync("prov-renew");
        var before = await LoadServiceAsync(serviceId);

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().RenewAsync(serviceId, 30));

        var after = await LoadServiceAsync(serviceId);

        // Renewing from today would silently take 30 days off a customer who renewed early.
        Assert.Equal(before.ExpiresAt!.Value.AddDays(30), after.ExpiresAt!.Value);
    }

    [Fact]
    public async Task Decommissioning_removes_the_client_and_frees_the_slot()
    {
        var userId = await _factory.CreateMemberAsync("prov-decom-member");
        var productId = await _factory.CreateProductAsync("prov-decom-product");
        var serverId = await CreateServerAsync("prov-decom-server", country: "NL");
        var planId = await CreatePlanAsync(productId, "prov-decom-plan", country: "NL");

        var serviceId = await _factory.WithScopeAsync(async services =>
            (await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null))).Value);

        await RunJobsAsync();

        var reserved = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>().VpnServers.AsNoTracking()
                .Where(server => server.Id == serverId)
                .Select(server => server.ReservedClients)
                .FirstAsync());

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().DecommissionAsync(serviceId));

        await RunJobsAsync();

        var service = await LoadServiceAsync(serviceId);
        Assert.Equal(CustomerServiceStatus.Ended, service.Status);
        Assert.False(_factory.Panel.Clients.ContainsKey(service.PanelClientEmail!));

        var freed = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>().VpnServers.AsNoTracking()
                .Where(server => server.Id == serverId)
                .Select(server => server.ReservedClients)
                .FirstAsync());

        // Released only once the client is confirmed gone.
        Assert.Equal(reserved - 1, freed);
    }

    // ------------------------------------------------------------------------- usage sync ----

    [Fact]
    public async Task Usage_is_pulled_from_the_panel()
    {
        var serviceId = await ProvisionedServiceAsync("prov-usage");
        var service = await LoadServiceAsync(serviceId);

        _factory.Panel.SetTraffic(PanelUrl, service.PanelClientEmail!, 1_000_000, 2_000_000, FiftyGibibytes);

        Assert.Equal(1, await SyncUsageAsync());

        var synced = await LoadServiceAsync(serviceId);

        Assert.Equal(3_000_000, synced.UsedBytes);
        Assert.NotNull(synced.LastUsageSyncAt);
        Assert.Equal(CustomerServiceStatus.Active, synced.Status);
    }

    [Fact]
    public async Task A_service_past_its_quota_becomes_exhausted()
    {
        var serviceId = await ProvisionedServiceAsync("prov-exhausted");
        var service = await LoadServiceAsync(serviceId);

        _factory.Panel.SetTraffic(PanelUrl, service.PanelClientEmail!, FiftyGibibytes, 1, FiftyGibibytes);

        await SyncUsageAsync();

        Assert.Equal(CustomerServiceStatus.Exhausted, (await LoadServiceAsync(serviceId)).Status);
    }

    [Fact]
    public async Task A_suspended_service_is_not_revived_by_a_usage_sync()
    {
        // An operator withheld it. Running out of quota — or not — does not change that decision.
        var serviceId = await ProvisionedServiceAsync("prov-suspend-sync");
        var service = await LoadServiceAsync(serviceId);

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().SuspendAsync(serviceId));
        await RunJobsAsync();

        _factory.Panel.SetTraffic(PanelUrl, service.PanelClientEmail!, 10, 10, FiftyGibibytes);
        await SyncUsageAsync();

        Assert.Equal(CustomerServiceStatus.Suspended, (await LoadServiceAsync(serviceId)).Status);
    }

    [Fact]
    public async Task A_client_that_vanished_from_the_panel_is_flagged_for_an_operator()
    {
        var serviceId = await ProvisionedServiceAsync("prov-vanished");
        var service = await LoadServiceAsync(serviceId);

        // Removed behind the portal's back — somebody deleted it in the panel's own UI.
        _factory.Panel.RemoveClient(PanelUrl, service.PanelClientEmail!);

        await SyncUsageAsync();

        var flagged = await LoadServiceAsync(serviceId);

        Assert.Equal(CustomerServiceStatus.NeedsAttention, flagged.Status);
        Assert.Contains("no longer present", flagged.LastError!, StringComparison.OrdinalIgnoreCase);
    }
}
