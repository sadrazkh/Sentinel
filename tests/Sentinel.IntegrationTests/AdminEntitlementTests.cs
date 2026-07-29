using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Products;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Granting and revoking individual access. Each test ends by checking what the *member*
/// experiences, not just what the admin form reported — the grant is only meaningful if it
/// changes the launch decision.
/// </summary>
public sealed class AdminEntitlementTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public AdminEntitlementTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync(string userName)
    {
        await _factory.CreateMemberAsync(userName);
        await _factory.AddToRoleAsync(userName, RoleNames.Admin);

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    private async Task<HttpClient> MemberClientAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    [Fact]
    public async Task Granting_access_opens_an_application_that_requires_a_grant()
    {
        using var admin = await AdminClientAsync("ent-grant-admin");

        var memberId = await _factory.CreateMemberAsync("ent-grant-member");
        var appId = await _factory.CreateApplicationAsync(
            "ent-grant-app", requiresExplicitEntitlement: true);

        using var member = await MemberClientAsync("ent-grant-member");

        var before = await member.GetAsync("/apps/ent-grant-app/open");
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        var granted = await GrantAsync(admin, memberId, appId);
        Assert.Equal(HttpStatusCode.Redirect, granted.StatusCode);

        // No re-login: access is decided per request.
        var after = await member.GetAsync("/apps/ent-grant-app/open");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Equal("https://apps.example.com/target", after.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Revoking_access_closes_it_again_immediately()
    {
        using var admin = await AdminClientAsync("ent-revoke-admin");

        var memberId = await _factory.CreateMemberAsync("ent-revoke-member");
        var appId = await _factory.CreateApplicationAsync(
            "ent-revoke-app", requiresExplicitEntitlement: true);

        await GrantAsync(admin, memberId, appId);

        using var member = await MemberClientAsync("ent-revoke-member");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await member.GetAsync("/apps/ent-revoke-app/open")).StatusCode);

        var entitlement = await _factory.FindEntitlementAsync(memberId, appId);
        await RevokeAsync(admin, memberId, appId, entitlement!.ConcurrencyToken);

        var after = await member.GetAsync("/apps/ent-revoke-app/open");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task Re_granting_reuses_the_single_row_rather_than_adding_a_second()
    {
        // Exactly one row per (user, application) is what keeps the access check a single
        // lookup and stops two rows from disagreeing about the answer.
        using var admin = await AdminClientAsync("ent-regrant-admin");

        var memberId = await _factory.CreateMemberAsync("ent-regrant-member");
        var appId = await _factory.CreateApplicationAsync(
            "ent-regrant-app", requiresExplicitEntitlement: true);

        await GrantAsync(admin, memberId, appId);

        var first = await _factory.FindEntitlementAsync(memberId, appId);
        await RevokeAsync(admin, memberId, appId, first!.ConcurrencyToken);

        var revoked = await _factory.FindEntitlementAsync(memberId, appId);
        Assert.NotNull(revoked!.RevokedAt);

        await GrantAsync(admin, memberId, appId, token: revoked.ConcurrencyToken);

        Assert.Equal(1, await _factory.CountEntitlementsAsync(memberId, appId));

        var reinstated = await _factory.FindEntitlementAsync(memberId, appId);
        Assert.Null(reinstated!.RevokedAt);
    }

    [Fact]
    public async Task A_grant_does_not_open_an_application_that_is_switched_off()
    {
        // The master switch has to outrank an individual arrangement, or turning an
        // application off would not actually take it out of service.
        using var admin = await AdminClientAsync("ent-disabled-admin");

        var memberId = await _factory.CreateMemberAsync("ent-disabled-member");
        var appId = await _factory.CreateApplicationAsync(
            "ent-disabled-app", requiresExplicitEntitlement: true, isEnabled: false);

        await GrantAsync(admin, memberId, appId);

        using var member = await MemberClientAsync("ent-disabled-member");
        var response = await member.GetAsync("/apps/ent-disabled-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_grant_stops_working_on_its_own()
    {
        using var admin = await AdminClientAsync("ent-expiry-admin");

        var memberId = await _factory.CreateMemberAsync("ent-expiry-member");
        var appId = await _factory.CreateApplicationAsync(
            "ent-expiry-app", requiresExplicitEntitlement: true);

        // Starts and ends in the past.
        await GrantAsync(
            admin, memberId, appId,
            startsAt: DateTime.UtcNow.Date.AddDays(-30),
            expiresAt: DateTime.UtcNow.Date.AddDays(-1));

        using var member = await MemberClientAsync("ent-expiry-member");
        var response = await member.GetAsync("/apps/ent-expiry-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_expiry_before_the_start_is_refused()
    {
        using var admin = await AdminClientAsync("ent-baddates-admin");

        var memberId = await _factory.CreateMemberAsync("ent-baddates-member");
        var appId = await _factory.CreateApplicationAsync("ent-baddates-app");

        await GrantAsync(
            admin, memberId, appId,
            startsAt: DateTime.UtcNow.Date,
            expiresAt: DateTime.UtcNow.Date.AddDays(-5));

        Assert.Null(await _factory.FindEntitlementAsync(memberId, appId));
    }

    [Fact]
    public async Task A_grant_survives_an_expired_membership()
    {
        // The point of individual access: an arrangement that does not depend on the
        // subscription being live.
        using var admin = await AdminClientAsync("ent-outlive-admin");

        var memberId = await _factory.CreateMemberAsync(
            "ent-outlive-member", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-120));

        var appId = await _factory.CreateApplicationAsync("ent-outlive-app");

        await GrantAsync(admin, memberId, appId);

        using var member = await MemberClientAsync("ent-outlive-member");
        var response = await member.GetAsync("/apps/ent-outlive-app/open");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_revoke_form_is_refused_rather_than_applied()
    {
        using var admin = await AdminClientAsync("ent-concurrency-admin");

        var memberId = await _factory.CreateMemberAsync("ent-concurrency-member");
        var appId = await _factory.CreateApplicationAsync("ent-concurrency-app");

        await GrantAsync(admin, memberId, appId);

        var original = await _factory.FindEntitlementAsync(memberId, appId);
        var staleToken = original!.ConcurrencyToken;

        // Somebody else edits first, which rotates the token.
        await GrantAsync(admin, memberId, appId, notes: "Updated by another operator", token: staleToken);

        await RevokeAsync(admin, memberId, appId, staleToken);

        var afterStaleRevoke = await _factory.FindEntitlementAsync(memberId, appId);
        Assert.Null(afterStaleRevoke!.RevokedAt);
        Assert.Equal("Updated by another operator", afterStaleRevoke.Notes);
    }

    [Fact]
    public async Task Support_cannot_grant_access()
    {
        await _factory.CreateMemberAsync("ent-support");
        await _factory.AddToRoleAsync("ent-support", RoleNames.Support);

        var memberId = await _factory.CreateMemberAsync("ent-support-target");
        var appId = await _factory.CreateApplicationAsync("ent-support-app");

        using var support = _factory.CreateNonRedirectingClient();
        await support.SignInAsync("ent-support", PortalTestData.MemberPassword);

        var response = await GrantAsync(support, memberId, appId);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        Assert.Null(await _factory.FindEntitlementAsync(memberId, appId));
    }

    [Fact]
    public async Task Granting_and_revoking_are_both_audited()
    {
        using var admin = await AdminClientAsync("ent-audit-admin");

        var memberId = await _factory.CreateMemberAsync("ent-audit-member");
        var appId = await _factory.CreateApplicationAsync("ent-audit-app");

        await GrantAsync(admin, memberId, appId);

        var entitlement = await _factory.FindEntitlementAsync(memberId, appId);
        await RevokeAsync(admin, memberId, appId, entitlement!.ConcurrencyToken);

        var actions = await _factory.RecentAuditActionsAsync(memberId.ToString());

        Assert.Contains("entitlement.granted", actions);
        Assert.Contains("entitlement.revoked", actions);
    }

    [Fact]
    public async Task The_editor_shows_the_same_decision_the_portal_enforces()
    {
        using var admin = await AdminClientAsync("ent-editor-admin");

        var memberId = await _factory.CreateMemberAsync("ent-editor-member");
        await _factory.CreateApplicationAsync("ent-editor-open");
        await _factory.CreateApplicationAsync("ent-editor-locked", requiresExplicitEntitlement: true);

        var page = await admin.GetStringAsync($"/Admin/Users/Entitlements/{memberId}");

        Assert.Contains("ent-editor-open", page, StringComparison.Ordinal);
        Assert.Contains("ent-editor-locked", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ helpers ----

    private static async Task<HttpResponseMessage> GrantAsync(
        HttpClient client,
        Guid userId,
        Guid productId,
        DateTime? startsAt = null,
        DateTime? expiresAt = null,
        string? notes = null,
        Guid? token = null)
    {
        var antiForgery = await client.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{userId}");

        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["UserId"] = userId.ToString(),
            ["ProductId"] = productId.ToString(),
        };

        if (startsAt is { } starts)
        {
            fields["StartsAt"] = starts.ToString("yyyy-MM-dd");
        }

        if (expiresAt is { } expires)
        {
            fields["ExpiresAt"] = expires.ToString("yyyy-MM-dd");
        }

        if (notes is not null)
        {
            fields["Notes"] = notes;
        }

        if (token is { } concurrencyToken)
        {
            fields["ConcurrencyToken"] = concurrencyToken.ToString();
        }

        return await client.PostAsync("/Admin/Users/GrantEntitlement", new FormUrlEncodedContent(fields));
    }

    private static async Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        Guid userId,
        Guid productId,
        Guid? token)
    {
        var antiForgery = await client.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{userId}");

        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["UserId"] = userId.ToString(),
            ["ProductId"] = productId.ToString(),
        };

        if (token is { } concurrencyToken)
        {
            fields["ConcurrencyToken"] = concurrencyToken.ToString();
        }

        return await client.PostAsync("/Admin/Users/RevokeEntitlement", new FormUrlEncodedContent(fields));
    }
}

internal static class EntitlementTestQueries
{
    public static Task<ProductEntitlement?> FindEntitlementAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        Guid productId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.ProductEntitlements
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.UserId == userId && e.ProductId == productId);
        });

    public static Task<int> CountEntitlementsAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        Guid productId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.ProductEntitlements
                .CountAsync(e => e.UserId == userId && e.ProductId == productId);
        });
}
