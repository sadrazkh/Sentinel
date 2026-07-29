using System.Net;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed class AdminAuditTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public AdminAuditTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        await _factory.CreateMemberAsync(userName);
        await _factory.AddToRoleAsync(userName, role);

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    // -------------------------------------------------------------------- authorization ----

    [Fact]
    public async Task An_ordinary_member_cannot_read_the_audit_log()
    {
        // The audit trail records who did what to whom across every account. It is one of the
        // most sensitive reads in the system.
        await _factory.CreateMemberAsync("audit-plain-member");

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("audit-plain-member", PortalTestData.MemberPassword);

        var response = await client.GetAsync("/Admin/Audit");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_can_read_the_audit_log()
    {
        using var client = await SignedInAsync("audit-support", RoleNames.Support);

        var response = await client.GetAsync("/Admin/Audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Only_a_super_admin_reaches_the_system_page()
    {
        using var admin = await SignedInAsync("system-admin-only", RoleNames.Admin);
        var asAdmin = await admin.GetAsync("/Admin/System");

        Assert.Equal(HttpStatusCode.Redirect, asAdmin.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            asAdmin.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        using var superAdmin = await SignedInAsync("system-superadmin", RoleNames.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.GetAsync("/Admin/System")).StatusCode);
    }

    // -------------------------------------------------------------------------- content ----

    [Fact]
    public async Task A_sign_in_appears_in_the_audit_log()
    {
        await _factory.CreateMemberAsync("audit-visible-member");

        using var member = _factory.CreateNonRedirectingClient();
        await member.SignInAsync("audit-visible-member", PortalTestData.MemberPassword);

        using var support = await SignedInAsync("audit-reader", RoleNames.Support);
        var page = await support.GetStringAsync("/Admin/Audit?auditAction=auth.login.succeeded");

        Assert.Contains("auth.login.succeeded", ResultsTable(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filtering_by_action_excludes_everything_else()
    {
        await _factory.CreateMemberAsync("audit-filter-member");

        using var noise = _factory.CreateNonRedirectingClient();
        var token = await noise.GetAntiForgeryTokenAsync("/Account/Login");
        await noise.PostLoginAsync(token, "audit-filter-member", "Wrong-Password-5555");

        using var support = await SignedInAsync("audit-filter-reader", RoleNames.Support);

        var page = await support.GetStringAsync("/Admin/Audit?auditAction=auth.login.failed");

        // Only the results table is examined: the filter dropdown lists every known action, so
        // searching the whole page would match its own <option> elements.
        var results = ResultsTable(page);

        Assert.Contains("auth.login.failed", results, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.login.succeeded", results, StringComparison.Ordinal);
    }

    private static string ResultsTable(string html)
    {
        var start = html.IndexOf("<tbody>", StringComparison.Ordinal);
        var end = html.IndexOf("</tbody>", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "The audit results table was not found in the page.");

        return html[start..end];
    }

    [Fact]
    public async Task A_date_range_in_the_future_returns_nothing()
    {
        using var support = await SignedInAsync("audit-daterange", RoleNames.Support);

        var from = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd");
        var page = await support.GetStringAsync($"/Admin/Audit?From={from}");

        Assert.Contains("empty-state", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_log_never_renders_a_password()
    {
        // Metadata keys are screened at write time; this is the end-to-end confirmation that
        // nothing resembling a credential reaches the page.
        const string password = PortalTestData.MemberPassword;

        await _factory.CreateMemberAsync("audit-nosecrets");

        using var member = _factory.CreateNonRedirectingClient();
        var token = await member.GetAntiForgeryTokenAsync("/Account/Login");
        await member.PostLoginAsync(token, "audit-nosecrets", password);

        using var support = await SignedInAsync("audit-nosecrets-reader", RoleNames.Support);
        var page = await support.GetStringAsync("/Admin/Audit");

        Assert.DoesNotContain(password, page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordHash", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task There_is_no_endpoint_that_edits_or_deletes_an_audit_entry()
    {
        // An audit log the application can rewrite is not evidence of anything.
        using var superAdmin = await SignedInAsync("audit-immutable", RoleNames.SuperAdmin);
        var token = await superAdmin.GetAntiForgeryTokenAsync("/Admin/Audit");

        foreach (var path in new[] { "/Admin/Audit/Delete", "/Admin/Audit/Edit", "/Admin/Audit/Purge" })
        {
            var response = await superAdmin.PostAsync(path, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_system_page_shows_counters_without_exposing_secrets()
    {
        using var superAdmin = await SignedInAsync("system-secrets", RoleNames.SuperAdmin);

        var page = await superAdmin.GetStringAsync("/Admin/System");

        Assert.Contains("Database:Provider", page, StringComparison.Ordinal);

        // Connection strings, key material and the seed password are absent rather than masked.
        Assert.DoesNotContain("ConnectionStrings", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyRingPath", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SentinelWebApplicationFactory.AdminPassword, page, StringComparison.Ordinal);
        Assert.DoesNotContain(PortalTestData.MemberPassword, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_system_page_lists_every_role_with_its_member_count()
    {
        using var superAdmin = await SignedInAsync("system-roles", RoleNames.SuperAdmin);

        var page = await superAdmin.GetStringAsync("/Admin/System");

        foreach (var role in RoleNames.All)
        {
            Assert.Contains(role, page, StringComparison.Ordinal);
        }
    }
}
