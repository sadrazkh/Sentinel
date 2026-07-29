using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The catalogue admin, with the upload path as its centre of gravity: a file upload is the
/// one place where an authenticated but untrusted party gets bytes onto our disk and back out
/// through our origin.
/// </summary>
public sealed class AdminApplicationTests : IClassFixture<SentinelWebApplicationFactory>
{
    /// <summary>A real, minimal 1×1 PNG — signature, IHDR, IDAT and IEND.</summary>
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly SentinelWebApplicationFactory _factory;

    public AdminApplicationTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync(string userName)
    {
        await _factory.CreateMemberAsync(userName);
        await _factory.AddToRoleAsync(userName, RoleNames.Admin);

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    // ------------------------------------------------------------------- authorization ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_catalogue_admin()
    {
        await _factory.CreateMemberAsync("app-admin-member");

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("app-admin-member", PortalTestData.MemberPassword);

        var response = await client.GetAsync("/Admin/Applications");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_can_list_but_cannot_create()
    {
        await _factory.CreateMemberAsync("app-admin-support");
        await _factory.AddToRoleAsync("app-admin-support", RoleNames.Support);

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("app-admin-support", PortalTestData.MemberPassword);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Applications")).StatusCode);

        var create = await client.GetAsync("/Admin/Applications/Create");
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            create.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------- create ----

    [Fact]
    public async Task An_administrator_can_create_an_application_that_members_then_see()
    {
        using var admin = await AdminClientAsync("app-create-admin");

        var response = await CreateApplicationAsync(admin, "created-app");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        await _factory.CreateMemberAsync("app-create-viewer");
        using var member = _factory.CreateNonRedirectingClient();
        await member.SignInAsync("app-create-viewer", PortalTestData.MemberPassword);

        var page = await member.GetStringAsync("/Apps");
        Assert.Contains("created-app", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("/relative/path")]
    [InlineData("https://user:secret@apps.example.com/x")]
    public async Task A_dangerous_launch_url_is_refused(string launchUrl)
    {
        // The launch endpoint issues a redirect the browser follows, so this is exactly where
        // a javascript: URL would execute. It never reaches storage.
        using var admin = await AdminClientAsync($"app-url-{Math.Abs(launchUrl.GetHashCode())}");

        var key = $"badurl-{Math.Abs(launchUrl.GetHashCode())}";
        var response = await CreateApplicationAsync(admin, key, launchUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await _factory.FindApplicationAsync(key));
    }

    [Fact]
    public async Task Plain_http_to_a_public_host_is_refused()
    {
        using var admin = await AdminClientAsync("app-url-http");

        var response = await CreateApplicationAsync(admin, "http-app", "http://apps.example.com/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await _factory.FindApplicationAsync("http-app"));
    }

    [Theory]
    [InlineData("Upper-Case")]
    [InlineData("with space")]
    [InlineData("../escape")]
    public async Task An_invalid_key_is_refused(string key)
    {
        using var admin = await AdminClientAsync($"app-key-{Math.Abs(key.GetHashCode())}");

        var response = await CreateApplicationAsync(admin, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_key_is_refused()
    {
        using var admin = await AdminClientAsync("app-key-duplicate");

        await CreateApplicationAsync(admin, "duplicate-key-app");
        var second = await CreateApplicationAsync(admin, "duplicate-key-app");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var count = await _factory.CountApplicationsAsync("duplicate-key-app");
        Assert.Equal(1, count);
    }

    // -------------------------------------------------------------------- icon upload ----

    [Fact]
    public async Task A_real_png_is_accepted_and_served_back_as_an_image()
    {
        using var admin = await AdminClientAsync("icon-upload-admin");
        await CreateApplicationAsync(admin, "icon-app");

        var application = await _factory.FindApplicationAsync("icon-app");
        Assert.NotNull(application);

        var upload = await UploadIconAsync(admin, application!.Id, ValidPng, "logo.png", "image/png");
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);

        var stored = await _factory.FindApplicationAsync("icon-app");
        Assert.NotNull(stored!.IconPath);

        var icon = await admin.GetAsync($"/media/app-icon/icon-app?v={stored.IconPath![..8]}");

        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ValidPng, await icon.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_script_payload_wearing_a_png_name_and_type_is_rejected()
    {
        // The file name says .png and the browser says image/png. Only the bytes are consulted.
        using var admin = await AdminClientAsync("icon-fake-admin");
        await CreateApplicationAsync(admin, "icon-fake-app");

        var application = await _factory.FindApplicationAsync("icon-fake-app");

        var payload = Encoding.UTF8.GetBytes("<!DOCTYPE html><script>alert(document.cookie)</script>");
        await UploadIconAsync(admin, application!.Id, payload, "logo.png", "image/png");

        var stored = await _factory.FindApplicationAsync("icon-fake-app");
        Assert.Null(stored!.IconPath);
    }

    [Fact]
    public async Task An_svg_is_rejected_even_though_it_is_a_real_image_format()
    {
        using var admin = await AdminClientAsync("icon-svg-admin");
        await CreateApplicationAsync(admin, "icon-svg-app");

        var application = await _factory.FindApplicationAsync("icon-svg-app");

        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        await UploadIconAsync(admin, application!.Id, svg, "logo.svg", "image/svg+xml");

        var stored = await _factory.FindApplicationAsync("icon-svg-app");
        Assert.Null(stored!.IconPath);
    }

    [Fact]
    public async Task An_oversized_upload_is_rejected()
    {
        using var admin = await AdminClientAsync("icon-large-admin");
        await CreateApplicationAsync(admin, "icon-large-app");

        var application = await _factory.FindApplicationAsync("icon-large-app");

        // A valid PNG header followed by a megabyte of padding: the format is fine, the size
        // is not, and the size limit is what stops storage exhaustion.
        var oversized = new byte[1024 * 1024];
        ValidPng.CopyTo(oversized, 0);

        await UploadIconAsync(admin, application!.Id, oversized, "big.png", "image/png");

        var stored = await _factory.FindApplicationAsync("icon-large-app");
        Assert.Null(stored!.IconPath);
    }

    [Fact]
    public async Task Replacing_an_icon_changes_the_url_so_a_cached_copy_cannot_persist()
    {
        using var admin = await AdminClientAsync("icon-replace-admin");
        await CreateApplicationAsync(admin, "icon-replace-app");

        var application = await _factory.FindApplicationAsync("icon-replace-app");

        await UploadIconAsync(admin, application!.Id, ValidPng, "one.png", "image/png");
        var first = (await _factory.FindApplicationAsync("icon-replace-app"))!.IconPath;

        await UploadIconAsync(admin, application.Id, ValidPng, "two.png", "image/png");
        var second = (await _factory.FindApplicationAsync("icon-replace-app"))!.IconPath;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    // --------------------------------------------------------------- serving the icon ----

    [Fact]
    public async Task An_anonymous_visitor_cannot_fetch_an_icon()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/media/app-icon/icon-app");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("/media/app-icon/..%2f..%2fappsettings.json")]
    [InlineData("/media/app-icon/../../appsettings.json")]
    [InlineData("/media/app-icon/..\\..\\appsettings.json")]
    [InlineData("/media/app-icon/no-such-app")]
    public async Task A_traversal_or_unknown_key_yields_nothing(string path)
    {
        await _factory.CreateMemberAsync("icon-traversal-viewer");

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("icon-traversal-viewer", PortalTestData.MemberPassword);

        var response = await client.GetAsync(path);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"Expected the request to be refused, got {response.StatusCode}.");
    }

    // ------------------------------------------------------------------------ helpers ----

    private static async Task<HttpResponseMessage> CreateApplicationAsync(
        HttpClient client,
        string key,
        string launchUrl = "https://apps.example.com/target")
    {
        var token = await client.GetAntiForgeryTokenAsync("/Admin/Applications/Create");

        return await client.PostAsync("/Admin/Applications/Create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Key"] = key,
                ["NameFa"] = $"برنامهٔ {key}",
                ["NameEn"] = $"Application {key}",
                ["LaunchUrl"] = launchUrl,
                ["PublishStatus"] = nameof(ApplicationPublishStatus.Published),
                ["IsEnabled"] = "true",
                ["DisplayOrder"] = "100",
            }));
    }

    private static async Task<HttpResponseMessage> UploadIconAsync(
        HttpClient client,
        Guid applicationId,
        byte[] content,
        string fileName,
        string contentType)
    {
        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Applications/Edit/{applicationId}");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(applicationId.ToString()), "ApplicationId" },
        };

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "Icon", fileName);

        return await client.PostAsync("/Admin/Applications/UploadIcon", form);
    }
}

internal static class ApplicationTestQueries
{
    public static Task<PortalApplication?> FindApplicationAsync(
        this SentinelWebApplicationFactory factory,
        string key) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.PortalApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Key == key);
        });

    public static Task<int> CountApplicationsAsync(
        this SentinelWebApplicationFactory factory,
        string key) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.PortalApplications.CountAsync(a => a.Key == key);
        });
}
