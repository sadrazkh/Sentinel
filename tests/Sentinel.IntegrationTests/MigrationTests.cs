using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Migration;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Moving a service between panels.
/// <para>
/// The property under test throughout is that the customer is never without a working client. That is
/// what forces the order — create at the destination, read it back, only then remove the source — and
/// most of these tests exist to prove that a failure at each step leaves the source intact.
/// </para>
/// </summary>
public sealed class MigrationTests : IClassFixture<VpnTestFactory>, IAsyncLifetime
{
    private const long FiftyGibibytes = 53_687_091_200L;
    private const long TenGibibytes = 10_737_418_240L;
    private const string PanelToken = "integration-only-panel-token-11223";

    private readonly VpnTestFactory _factory;

    public MigrationTests(VpnTestFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        _factory.Panel.AllCallsUnknown = false;
        _factory.Panel.ClearScripts();

        // The class shares one host and one database, and several tests here end with a migration
        // deliberately parked. Draining first keeps each test's own sweep counts its own.
        for (var pass = 0; pass < 10; pass++)
        {
            var moved = await RunJobsAsync() + await AdvanceAsync() + await ReconcileMigrationsAsync();

            if (moved == 0)
            {
                break;
            }
        }

        _factory.Panel.ClearScripts();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------------- fixtures ----

    /// <summary>Each server gets its own address, which is what makes them distinct panels.</summary>
    private static string UrlFor(string key) => $"https://{key}.panel.example.com:2053";

    private static int _countrySequence;

    /// <summary>
    /// A country code no other test in this class uses.
    /// <para>
    /// The class shares one database, so every server ever created is a selection candidate. Without
    /// this, a plan for "DE" would happily place a service on a previous test's German server, and
    /// the test would then be migrating between two servers it did not create.
    /// </para>
    /// </summary>
    private static string NextCountry()
    {
        var n = Interlocked.Increment(ref _countrySequence);

        return $"{(char)('A' + (n / 26) % 26)}{(char)('A' + n % 26)}";
    }

    private Task<Guid> CreateServerAsync(
        string key,
        string country,
        int maxClients = 10,
        int priority = 100) =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var created = await admin.SaveAsync(null, new VpnServerSaveRequest(
                key, $"سرور {key}", $"Server {key}", country,
                UrlFor(key), PanelToken,
                VpnServerStatus.Active, maxClients, priority, null, null));

            Assert.True(created.Succeeded, created.ErrorKey);

            var db = services.GetRequiredService<IVpnDbContext>();

            var server = await db.VpnServers.FirstAsync(candidate => candidate.Id == created.Value);
            server.Status = VpnServerStatus.Active;
            server.Health = VpnServerHealth.Healthy;

            db.ServerInboundProfiles.Add(new ServerInboundProfile
            {
                Id = Guid.NewGuid(),
                ServerId = created.Value,
                InboundId = 1,
                Label = "vless:443",
                Protocol = "vless",
                IsEnabled = true,
            });

            await db.SaveChangesAsync();

            return created.Value;
        });

    private Task<Guid> CreatePlanAsync(Guid productId, string key, string? country) =>
        _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IServicePlanAdminService>()
                .SaveAsync(null, new ServicePlanSaveRequest(
                    key, productId, $"پلن {key}", $"Plan {key}", null, null,
                    FiftyGibibytes, 30, 2, 1_000_000, "IRR",
                    true, true, country, 100, false, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    private Task<int> RunJobsAsync(int batch = 20) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IProvisioningExecutor>().RunPendingAsync(batch));

    private Task<int> AdvanceAsync(int batch = 20) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IMigrationExecutor>().RunPendingAsync(batch));

    private Task<int> ReconcileMigrationsAsync(int batch = 20) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IMigrationExecutor>().ReconcileAsync(batch));

    private Task<CustomerService> LoadServiceAsync(Guid serviceId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .FirstAsync(service => service.Id == serviceId));

    private Task<ServiceMigration> LoadMigrationAsync(Guid migrationId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .ServiceMigrations.AsNoTracking()
                .FirstAsync(migration => migration.Id == migrationId));

    private Task<int> ReservedOnAsync(Guid serverId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .VpnServers.AsNoTracking()
                .Where(server => server.Id == serverId)
                .Select(server => server.ReservedClients)
                .FirstAsync());

    /// <summary>A live service on <c>{prefix}-source</c>, plus an empty <c>{prefix}-dest</c>.</summary>
    private async Task<Fixture> LiveServiceAsync(string prefix, long usedBytes = 0)
    {
        var userId = await _factory.CreateMemberAsync($"{prefix}-member");
        var productId = await _factory.CreateProductAsync($"{prefix}-product");
        var country = NextCountry();

        // Priority puts the source first, so the initial provisioning lands there and not on the
        // destination — otherwise the test would be migrating in whichever direction won a tie.
        var sourceId = await CreateServerAsync($"{prefix}-source", country, priority: 10);
        var destinationId = await CreateServerAsync($"{prefix}-dest", country, priority: 20);

        var planId = await CreatePlanAsync(productId, $"{prefix}-plan", country);

        var serviceId = await _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

        await RunJobsAsync();

        var service = await LoadServiceAsync(serviceId);

        Assert.Equal(CustomerServiceStatus.Active, service.Status);
        Assert.Equal(sourceId, service.ServerId);

        if (usedBytes > 0)
        {
            _factory.Panel.SetTraffic(
                UrlFor($"{prefix}-source"), service.PanelClientEmail!, usedBytes, 0, FiftyGibibytes);
        }

        return new Fixture(
            serviceId,
            service.PanelClientEmail!,
            sourceId,
            UrlFor($"{prefix}-source"),
            destinationId,
            UrlFor($"{prefix}-dest"),
            country);
    }

    private sealed record Fixture(
        Guid ServiceId,
        string Email,
        Guid SourceServerId,
        string SourceUrl,
        Guid DestinationServerId,
        string DestinationUrl,
        string Country);

    private Task<Sentinel.Application.Common.OperationResult<Guid>> PlanAsync(
        Fixture fixture,
        Guid? destination = null,
        bool byCountry = false) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IServiceMigrationManager>()
                .PlanAsync(new MigrateServiceRequest(
                    fixture.ServiceId,
                    byCountry ? null : destination ?? fixture.DestinationServerId,
                    fixture.Country,
                    "test")));

    /// <summary>
    /// Runs the saga to a standstill, so a test asserts on an end state rather than a step.
    /// <para>
    /// Retry backoff is skipped between passes. In production a refused step waits fifteen seconds
    /// and then a minute, which is right for a panel that is briefly unhappy and useless in a test —
    /// without this, a drain stops after one refusal and the assertion is about the wrong state.
    /// Parked steps are untouched: those are not runnable at any time, which is the point of them.
    /// </para>
    /// </summary>
    private async Task DrainAsync()
    {
        for (var pass = 0; pass < 12; pass++)
        {
            await ClearBackoffAsync();

            if (await AdvanceAsync() == 0)
            {
                return;
            }
        }
    }

    private Task ClearBackoffAsync() =>
        _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();

            var waiting = await db.ServiceMigrations
                .Where(migration => migration.NextAttemptAt != null
                                    && (migration.Step == MigrationStep.Planned
                                        || migration.Step == MigrationStep.Creating
                                        || migration.Step == MigrationStep.Verifying
                                        || migration.Step == MigrationStep.Detaching))
                .ToListAsync();

            foreach (var migration in waiting)
            {
                migration.NextAttemptAt = null;
            }

            await db.SaveChangesAsync();
        });

    // ------------------------------------------------------------------------- happy path ----

    [Fact]
    public async Task A_migration_creates_at_the_destination_before_removing_the_source()
    {
        var fixture = await LiveServiceAsync("mig-happy");

        var planned = await PlanAsync(fixture);
        Assert.True(planned.Succeeded, planned.ErrorKey);

        // Step one: created at the destination. The source is untouched — the customer is still
        // being served throughout.
        Assert.Equal(1, await AdvanceAsync());

        Assert.True(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));
        Assert.True(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));
        Assert.Equal(MigrationStep.Verifying, (await LoadMigrationAsync(planned.Value)).Step);

        // Step two: verified. Still on both, and now recorded as such.
        Assert.Equal(1, await AdvanceAsync());

        var verified = await LoadMigrationAsync(planned.Value);

        Assert.Equal(MigrationStep.Detaching, verified.Step);
        Assert.NotNull(verified.DualActiveSince);
        Assert.True(verified.IsDualActive);
        Assert.Equal(2, _factory.Panel.PanelCountFor(fixture.Email));

        // Step three: the source goes.
        Assert.Equal(1, await AdvanceAsync());

        var completed = await LoadMigrationAsync(planned.Value);

        Assert.Equal(MigrationStep.Completed, completed.Step);
        Assert.False(completed.IsDualActive);
        Assert.False(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));
        Assert.True(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));

        var service = await LoadServiceAsync(fixture.ServiceId);

        Assert.Equal(fixture.DestinationServerId, service.ServerId);
        Assert.Equal(CustomerServiceStatus.Active, service.Status);
    }

    [Fact]
    public async Task The_remaining_allowance_is_carried_over_not_the_original()
    {
        // Ten gibibytes spent of fifty. The destination gets forty, and the counter restarts at zero
        // — the customer neither loses what is left nor gets their quota back.
        var fixture = await LiveServiceAsync("mig-remaining", usedBytes: TenGibibytes);

        var planned = await PlanAsync(fixture);
        Assert.True(planned.Succeeded, planned.ErrorKey);

        var migration = await LoadMigrationAsync(planned.Value);

        Assert.Equal(FiftyGibibytes - TenGibibytes, migration.RemainingBytes);
        Assert.Equal(TenGibibytes, migration.SourceUsedBytes);

        await DrainAsync();

        var client = _factory.Panel.ClientOn(fixture.DestinationUrl, fixture.Email);

        Assert.NotNull(client);
        Assert.Equal(FiftyGibibytes - TenGibibytes, client!.TotalAllowanceBytes);

        var service = await LoadServiceAsync(fixture.ServiceId);

        Assert.Equal(FiftyGibibytes - TenGibibytes, service.TrafficBytes);
        Assert.Equal(0, service.UsedBytes);
    }

    [Fact]
    public async Task The_original_expiry_is_preserved()
    {
        // A migration is not a renewal. Re-deriving the expiry from the plan's duration would hand
        // the customer a fresh month, or take one off somebody halfway through theirs.
        var fixture = await LiveServiceAsync("mig-expiry");

        var before = (await LoadServiceAsync(fixture.ServiceId)).ExpiresAt;
        Assert.NotNull(before);

        var planned = await PlanAsync(fixture);
        await DrainAsync();

        var after = (await LoadServiceAsync(fixture.ServiceId)).ExpiresAt;
        var client = _factory.Panel.ClientOn(fixture.DestinationUrl, fixture.Email);

        Assert.Equal(before, after);
        Assert.NotNull(client!.ExpiresAt);
        Assert.Equal(before!.Value, client.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(MigrationStep.Completed, (await LoadMigrationAsync(planned.Value)).Step);
    }

    [Fact]
    public async Task An_unlimited_service_stays_unlimited()
    {
        // The panel expresses "no limit" as zero, so subtracting usage from it would migrate an
        // unlimited service onto a quota of nothing.
        var fixture = await LiveServiceAsync("mig-unlimited");

        await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();

            var service = await db.CustomerServices
                .FirstAsync(candidate => candidate.Id == fixture.ServiceId);

            service.TrafficBytes = 0;
            service.UsedBytes = TenGibibytes;

            await db.SaveChangesAsync();
        });

        var planned = await PlanAsync(fixture);
        Assert.True(planned.Succeeded, planned.ErrorKey);

        Assert.Equal(0, (await LoadMigrationAsync(planned.Value)).RemainingBytes);

        await DrainAsync();

        Assert.Equal(0, _factory.Panel.ClientOn(fixture.DestinationUrl, fixture.Email)!.TotalAllowanceBytes);
        Assert.Equal(0, (await LoadServiceAsync(fixture.ServiceId)).TrafficBytes);
    }

    [Fact]
    public async Task The_source_delete_keeps_its_traffic_record()
    {
        // Unlike a decommission. The source's counters are the record of what the customer used
        // before the move, and a support question a week later is answered from them.
        var fixture = await LiveServiceAsync("mig-keeptraffic", usedBytes: TenGibibytes);

        await PlanAsync(fixture);
        await DrainAsync();

        Assert.True(_factory.Panel.HasRetainedTraffic(fixture.SourceUrl, fixture.Email));
    }

    [Fact]
    public async Task Capacity_moves_with_the_service()
    {
        var fixture = await LiveServiceAsync("mig-capacity");

        Assert.Equal(1, await ReservedOnAsync(fixture.SourceServerId));
        Assert.Equal(0, await ReservedOnAsync(fixture.DestinationServerId));

        await PlanAsync(fixture);

        // Both held while the migration runs: the customer really does occupy a slot on each.
        Assert.Equal(1, await ReservedOnAsync(fixture.SourceServerId));
        Assert.Equal(1, await ReservedOnAsync(fixture.DestinationServerId));

        await DrainAsync();

        // The source is released only once its client is confirmed gone.
        Assert.Equal(0, await ReservedOnAsync(fixture.SourceServerId));
        Assert.Equal(1, await ReservedOnAsync(fixture.DestinationServerId));
    }

    // ---------------------------------------------------------- the source survives failure ----

    [Fact]
    public async Task A_destination_that_refuses_the_create_never_touches_the_source()
    {
        var fixture = await LiveServiceAsync("mig-create-refused");

        var planned = await PlanAsync(fixture);

        // Refused every time, so the step exhausts its retries and the migration is abandoned.
        for (var i = 0; i < ServiceMigration.MaxAttempts + 1; i++)
        {
            _factory.Panel.ScriptOnce("create", PanelOutcome.Rejected);
        }

        await DrainAsync();

        Assert.Equal(MigrationStep.Abandoned, (await LoadMigrationAsync(planned.Value)).Step);

        // The whole point: the customer is still being served by the source.
        Assert.True(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));
        Assert.Equal(fixture.SourceServerId, (await LoadServiceAsync(fixture.ServiceId)).ServerId);

        // And the destination's slot goes back, because nothing was ever put there.
        Assert.Equal(0, await ReservedOnAsync(fixture.DestinationServerId));
    }

    [Fact]
    public async Task A_create_that_answered_yes_but_produced_nothing_is_caught_by_the_read_back()
    {
        // The reason verification exists as its own step. Without it, "create returned success" would
        // be followed straight away by deleting the customer's only working client.
        var fixture = await LiveServiceAsync("mig-phantom");

        var planned = await PlanAsync(fixture);

        Assert.Equal(1, await AdvanceAsync());
        Assert.Equal(MigrationStep.Verifying, (await LoadMigrationAsync(planned.Value)).Step);

        // The client evaporates between the create and the read-back.
        _factory.Panel.RemoveClient(fixture.DestinationUrl, fixture.Email);

        Assert.Equal(1, await AdvanceAsync());

        var migration = await LoadMigrationAsync(planned.Value);

        // Still verifying, not detaching. The source has not been touched.
        Assert.Equal(MigrationStep.Verifying, migration.Step);
        Assert.Null(migration.DualActiveSince);
        Assert.True(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));
    }

    [Fact]
    public async Task A_destination_with_the_wrong_terms_is_corrected_before_the_source_goes()
    {
        var fixture = await LiveServiceAsync("mig-wrongterms", usedBytes: TenGibibytes);

        var planned = await PlanAsync(fixture);

        Assert.Equal(1, await AdvanceAsync());

        // Somebody edits the destination client in the panel's own UI, giving it the wrong quota.
        _factory.Panel.PlantClient(fixture.DestinationUrl, fixture.Email, [1]);

        Assert.Equal(1, await AdvanceAsync());

        // Not advanced: the mismatch is corrected and re-checked rather than trusted.
        Assert.Equal(MigrationStep.Verifying, (await LoadMigrationAsync(planned.Value)).Step);
        Assert.True(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));

        await DrainAsync();

        var client = _factory.Panel.ClientOn(fixture.DestinationUrl, fixture.Email);

        Assert.Equal(FiftyGibibytes - TenGibibytes, client!.TotalAllowanceBytes);
        Assert.Equal(MigrationStep.Completed, (await LoadMigrationAsync(planned.Value)).Step);
    }

    // -------------------------------------------------------------------- unknown outcomes ----

    [Fact]
    public async Task A_create_with_an_unknown_outcome_is_parked_and_never_repeated()
    {
        var fixture = await LiveServiceAsync("mig-create-unknown");

        var planned = await PlanAsync(fixture);

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);

        Assert.Equal(1, await AdvanceAsync());

        var parked = await LoadMigrationAsync(planned.Value);

        Assert.Equal(MigrationStep.NeedsAttention, parked.Step);

        // Not runnable, so no sweep will repeat the call that is in doubt.
        Assert.Equal(0, await AdvanceAsync());
        Assert.True(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));
    }

    [Fact]
    public async Task Reconciliation_resumes_a_lost_create_that_had_actually_landed()
    {
        var fixture = await LiveServiceAsync("mig-create-landed");

        var planned = await PlanAsync(fixture);

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);
        await AdvanceAsync();

        Assert.Equal(MigrationStep.NeedsAttention, (await LoadMigrationAsync(planned.Value)).Step);

        // The truth: the create did land.
        _factory.Panel.PlantClient(fixture.DestinationUrl, fixture.Email, [1]);

        Assert.Equal(1, await ReconcileMigrationsAsync());

        // On both panels, so the saga resumes at verification — not by creating a second client.
        Assert.Equal(MigrationStep.Verifying, (await LoadMigrationAsync(planned.Value)).Step);

        await DrainAsync();

        Assert.Equal(MigrationStep.Completed, (await LoadMigrationAsync(planned.Value)).Step);
        Assert.Equal(1, _factory.Panel.PanelCountFor(fixture.Email));
    }

    [Fact]
    public async Task Reconciliation_restarts_a_lost_create_that_had_not_landed()
    {
        var fixture = await LiveServiceAsync("mig-create-lost");

        var planned = await PlanAsync(fixture);

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);
        await AdvanceAsync();

        // Only the source has it, so nothing was created and starting again is safe.
        Assert.Equal(1, await ReconcileMigrationsAsync());
        Assert.Equal(MigrationStep.Creating, (await LoadMigrationAsync(planned.Value)).Step);

        await DrainAsync();

        Assert.Equal(MigrationStep.Completed, (await LoadMigrationAsync(planned.Value)).Step);
        Assert.True(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));
    }

    [Fact]
    public async Task A_lost_source_delete_that_actually_landed_is_completed_not_repeated()
    {
        var fixture = await LiveServiceAsync("mig-delete-landed");

        var planned = await PlanAsync(fixture);

        await AdvanceAsync();
        await AdvanceAsync();

        Assert.Equal(MigrationStep.Detaching, (await LoadMigrationAsync(planned.Value)).Step);

        _factory.Panel.ScriptOnce("delete", PanelOutcome.UnknownOutcome);
        Assert.Equal(1, await AdvanceAsync());

        Assert.Equal(MigrationStep.NeedsAttention, (await LoadMigrationAsync(planned.Value)).Step);

        // The truth: the delete did land.
        _factory.Panel.RemoveClient(fixture.SourceUrl, fixture.Email);

        Assert.Equal(1, await ReconcileMigrationsAsync());

        var migration = await LoadMigrationAsync(planned.Value);
        var service = await LoadServiceAsync(fixture.ServiceId);

        Assert.Equal(MigrationStep.Completed, migration.Step);
        Assert.Equal(fixture.DestinationServerId, service.ServerId);
        Assert.Equal(0, await ReservedOnAsync(fixture.SourceServerId));
    }

    [Fact]
    public async Task A_client_on_neither_panel_is_handed_to_an_operator()
    {
        // No step of this saga produces that state, so something outside the portal removed it.
        // Guessing which way to go from here is exactly what must not happen.
        var fixture = await LiveServiceAsync("mig-nowhere");

        var planned = await PlanAsync(fixture);

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);
        await AdvanceAsync();

        _factory.Panel.RemoveClient(fixture.SourceUrl, fixture.Email);

        Assert.Equal(1, await ReconcileMigrationsAsync());

        Assert.Equal(MigrationStep.Abandoned, (await LoadMigrationAsync(planned.Value)).Step);
        Assert.Equal(
            CustomerServiceStatus.NeedsAttention, (await LoadServiceAsync(fixture.ServiceId)).Status);
    }

    [Fact]
    public async Task A_panel_that_cannot_be_read_leaves_the_migration_parked()
    {
        // Both readings are needed, because the decision depends on the pair. One silent panel means
        // no decision at all — not a decision made on half the evidence.
        var fixture = await LiveServiceAsync("mig-silent");

        var planned = await PlanAsync(fixture);

        _factory.Panel.ScriptOnce("create", PanelOutcome.UnknownOutcome);
        await AdvanceAsync();

        _factory.Panel.AllCallsUnknown = true;

        Assert.Equal(0, await ReconcileMigrationsAsync());
        Assert.Equal(MigrationStep.NeedsAttention, (await LoadMigrationAsync(planned.Value)).Step);
    }

    // ------------------------------------------------------------------------- what is refused ----

    [Fact]
    public async Task A_second_migration_of_the_same_service_is_refused()
    {
        var fixture = await LiveServiceAsync("mig-double");

        Assert.True((await PlanAsync(fixture)).Succeeded);

        var second = await PlanAsync(fixture);

        Assert.False(second.Succeeded);
        Assert.Equal(MigrationErrors.AlreadyInFlight, second.ErrorKey);
    }

    [Fact]
    public async Task Migrating_to_the_server_it_is_already_on_is_refused()
    {
        var fixture = await LiveServiceAsync("mig-same");

        var result = await PlanAsync(fixture, destination: fixture.SourceServerId);

        Assert.False(result.Succeeded);
        Assert.Equal(MigrationErrors.SameServer, result.ErrorKey);
    }

    [Fact]
    public async Task A_full_destination_is_refused_before_anything_is_created()
    {
        var fixture = await LiveServiceAsync("mig-full");

        await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();

            var server = await db.VpnServers
                .FirstAsync(candidate => candidate.Id == fixture.DestinationServerId);

            server.ReservedClients = server.MaxClients;

            await db.SaveChangesAsync();
        });

        var result = await PlanAsync(fixture);

        Assert.False(result.Succeeded);
        Assert.Equal(MigrationErrors.NoCapacity, result.ErrorKey);
        Assert.False(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));
    }

    [Fact]
    public async Task A_destination_with_no_usable_inbound_is_refused()
    {
        var fixture = await LiveServiceAsync("mig-noinbound");

        await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();

            foreach (var profile in await db.ServerInboundProfiles
                         .Where(profile => profile.ServerId == fixture.DestinationServerId)
                         .ToListAsync())
            {
                profile.IsEnabled = false;
            }

            await db.SaveChangesAsync();
        });

        var result = await PlanAsync(fixture);

        Assert.False(result.Succeeded);
        Assert.Equal(MigrationErrors.DestinationUnusable, result.ErrorKey);
    }

    [Fact]
    public async Task An_ended_service_cannot_be_migrated()
    {
        var fixture = await LiveServiceAsync("mig-ended");

        await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();

            var service = await db.CustomerServices
                .FirstAsync(candidate => candidate.Id == fixture.ServiceId);

            service.Status = CustomerServiceStatus.Ended;

            await db.SaveChangesAsync();
        });

        var result = await PlanAsync(fixture);

        Assert.False(result.Succeeded);
        Assert.Equal(MigrationErrors.NotMigratable, result.ErrorKey);
    }

    [Fact]
    public async Task A_source_panel_that_cannot_be_read_refuses_the_plan()
    {
        // The remaining allowance becomes the customer's new quota. Falling back to the cached
        // counter would mean guessing that number, which is not a reasonable failure mode.
        var fixture = await LiveServiceAsync("mig-unreadable");

        _factory.Panel.ScriptOnce("traffic", PanelOutcome.UnknownOutcome);

        var result = await PlanAsync(fixture);

        Assert.False(result.Succeeded);
        Assert.Equal(MigrationErrors.SourceUnreadable, result.ErrorKey);
    }

    [Fact]
    public async Task Lifecycle_operations_are_refused_while_a_migration_is_in_flight()
    {
        // Each of these queues a job against the server the service is on now — the source, which the
        // saga is about to delete the client from.
        var fixture = await LiveServiceAsync("mig-locked");

        Assert.True((await PlanAsync(fixture)).Succeeded);

        await _factory.WithScopeAsync(async services =>
        {
            var manager = services.GetRequiredService<ICustomerServiceManager>();

            foreach (var result in new[]
                     {
                         await manager.SuspendAsync(fixture.ServiceId),
                         await manager.RenewAsync(fixture.ServiceId, 30),
                         await manager.ResetTrafficAsync(fixture.ServiceId),
                         await manager.DecommissionAsync(fixture.ServiceId),
                     })
            {
                Assert.False(result.Succeeded);
                Assert.Equal(ServiceErrors.BusyMigrating, result.ErrorKey);
            }
        });

        // And the service is untouched by any of them.
        Assert.Equal(CustomerServiceStatus.Active, (await LoadServiceAsync(fixture.ServiceId)).Status);
    }

    // ------------------------------------------------------------------------------ rollback ----

    [Fact]
    public async Task A_migration_can_be_called_off_before_the_destination_is_verified()
    {
        var fixture = await LiveServiceAsync("mig-rollback");

        var planned = await PlanAsync(fixture);

        Assert.Equal(1, await AdvanceAsync());
        Assert.True(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));

        var rolled = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IServiceMigrationManager>().RollBackAsync(planned.Value));

        Assert.True(rolled.Succeeded, rolled.ErrorKey);

        var migration = await LoadMigrationAsync(planned.Value);

        Assert.Equal(MigrationStep.RolledBack, migration.Step);

        // The destination client is removed and its slot handed back; the source never moved.
        Assert.False(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));
        Assert.True(_factory.Panel.Has(fixture.SourceUrl, fixture.Email));
        Assert.Equal(0, await ReservedOnAsync(fixture.DestinationServerId));
        Assert.Equal(fixture.SourceServerId, (await LoadServiceAsync(fixture.ServiceId)).ServerId);
    }

    [Fact]
    public async Task A_migration_cannot_be_called_off_once_the_destination_is_verified()
    {
        // Past verification the customer may already be relying on the destination. Rolling back from
        // there would delete a client that could be their only working one.
        var fixture = await LiveServiceAsync("mig-noreverse");

        var planned = await PlanAsync(fixture);

        await AdvanceAsync();
        await AdvanceAsync();

        Assert.Equal(MigrationStep.Detaching, (await LoadMigrationAsync(planned.Value)).Step);

        var rolled = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IServiceMigrationManager>().RollBackAsync(planned.Value));

        Assert.False(rolled.Succeeded);
        Assert.Equal(MigrationErrors.NotRollbackable, rolled.ErrorKey);
        Assert.True(_factory.Panel.Has(fixture.DestinationUrl, fixture.Email));
    }

    // -------------------------------------------------------------------------- the operator ----

    [Fact]
    public async Task The_operator_view_shows_the_dual_active_window()
    {
        var fixture = await LiveServiceAsync("mig-view");

        var planned = await PlanAsync(fixture);

        await AdvanceAsync();
        await AdvanceAsync();

        var view = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IServiceMigrationManager>()
                .ActiveForServiceAsync(fixture.ServiceId));

        Assert.NotNull(view);
        Assert.Equal(planned.Value, view!.Id);
        Assert.True(view.IsDualActive);
        Assert.NotNull(view.DualActiveFor(DateTimeOffset.UtcNow));
        Assert.False(view.IsFinished);

        await DrainAsync();

        var finished = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IServiceMigrationManager>()
                .ActiveForServiceAsync(fixture.ServiceId));

        Assert.Null(finished);
    }

    [Fact]
    public async Task A_country_migration_picks_a_server_other_than_the_one_it_is_on()
    {
        // The usual case: an operator moves a customer off a failing box and does not care which
        // healthy one they land on — only that it is not the one they are leaving.
        var fixture = await LiveServiceAsync("mig-bycountry");

        var planned = await PlanAsync(fixture, byCountry: true);

        Assert.True(planned.Succeeded, planned.ErrorKey);

        var migration = await LoadMigrationAsync(planned.Value);

        Assert.NotEqual(fixture.SourceServerId, migration.DestinationServerId);
        Assert.Equal(fixture.DestinationServerId, migration.DestinationServerId);
    }
}
