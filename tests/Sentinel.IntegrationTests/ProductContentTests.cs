using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Products;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Product content over the real HTTP surface: who reads what, and what a refused download does.
/// </summary>
public sealed class ProductContentTests : IClassFixture<SentinelWebApplicationFactory>
{
    private const string DownloadUrl = "https://downloads.example.com/client.exe";

    private readonly SentinelWebApplicationFactory _factory;

    public ProductContentTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    private static async Task<string> GetEnglishAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Accept-Language", "en-US");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    // ------------------------------------------------------------------------ sections ----

    [Fact]
    public async Task A_public_section_is_readable_by_a_member_without_access()
    {
        // The pre-purchase audience: somebody deciding whether to obtain the product.
        await _factory.CreateMemberAsync("content-public");
        var productId = await _factory.CreateProductAsync(
            "content-public-product", requiresExplicitEntitlement: true);

        await _factory.AddSectionAsync(
            productId, ContentVisibility.Public, markupEn: "Public **overview** text.");

        using var client = await SignedInAsync("content-public");
        var page = await GetEnglishAsync(client, "/products/content-public-product");

        Assert.Contains("Public <strong>overview</strong> text.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entitled_section_is_hidden_from_a_member_without_usable_access()
    {
        await _factory.CreateMemberAsync("content-locked");
        var productId = await _factory.CreateProductAsync(
            "content-locked-product", requiresExplicitEntitlement: true);

        await _factory.AddSectionAsync(
            productId, ContentVisibility.Entitled, markupEn: "Secret setup host is vpn.internal.");

        using var client = await SignedInAsync("content-locked");
        var page = await GetEnglishAsync(client, "/products/content-locked-product");

        Assert.DoesNotContain("vpn.internal", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entitled_section_is_readable_once_access_is_granted()
    {
        var userId = await _factory.CreateMemberAsync("content-entitled");
        var productId = await _factory.CreateProductAsync(
            "content-entitled-product", requiresExplicitEntitlement: true);

        await _factory.AddSectionAsync(
            productId, ContentVisibility.Entitled, markupEn: "Setup host is vpn.example.com.");
        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync("content-entitled");
        var page = await GetEnglishAsync(client, "/products/content-entitled-product");

        Assert.Contains("vpn.example.com", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_section_is_hidden_even_from_an_entitled_member()
    {
        var userId = await _factory.CreateMemberAsync("content-internal");
        var productId = await _factory.CreateProductAsync(
            "content-internal-product", requiresExplicitEntitlement: true);

        await _factory.AddSectionAsync(
            productId, ContentVisibility.Internal, markupEn: "Draft note: not ready yet.");
        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync("content-internal");
        var page = await GetEnglishAsync(client, "/products/content-internal-product");

        Assert.DoesNotContain("Draft note", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stored_markup_reaches_the_page_as_the_tags_the_renderer_built()
    {
        await _factory.CreateMemberAsync("content-markup");
        var productId = await _factory.CreateProductAsync("content-markup-product");

        await _factory.AddSectionAsync(
            productId,
            ContentVisibility.Public,
            markupEn: "# Heading\n\n- one\n- two\n\nSee [docs](https://example.com/d).");

        using var client = await SignedInAsync("content-markup");
        var page = await GetEnglishAsync(client, "/products/content-markup-product");

        Assert.Contains("<h3>Heading</h3>", page, StringComparison.Ordinal);
        Assert.Contains("<li>one</li>", page, StringComparison.Ordinal);
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_typed_into_a_section_reaches_the_page_inert()
    {
        // Rendering happens on save, so the stored value already has no live tag in it. This is
        // the end-to-end proof that nothing later un-escapes it.
        await _factory.CreateMemberAsync("content-xss");
        var productId = await _factory.CreateProductAsync("content-xss-product");

        await _factory.AddSectionAsync(
            productId,
            ContentVisibility.Public,
            markupEn: "<script>alert(1)</script><img src=x onerror=alert(1)>");

        using var client = await SignedInAsync("content-xss");
        var page = await GetEnglishAsync(client, "/products/content-xss-product");

        Assert.DoesNotContain("<script>alert", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------- downloads ----

    [Fact]
    public async Task An_entitled_download_redirects_for_a_member_who_holds_the_product()
    {
        var userId = await _factory.CreateMemberAsync("dl-allowed");
        var productId = await _factory.CreateProductAsync(
            "dl-allowed-product", requiresExplicitEntitlement: true);

        var downloadId = await _factory.AddDownloadAsync(productId, ContentVisibility.Entitled);
        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync("dl-allowed");
        var response = await client.GetAsync($"/products/dl-allowed-product/downloads/{downloadId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(DownloadUrl, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task An_entitled_download_is_refused_without_access_and_never_leaks_its_url()
    {
        await _factory.CreateMemberAsync("dl-denied");
        var productId = await _factory.CreateProductAsync(
            "dl-denied-product", requiresExplicitEntitlement: true);

        var downloadId = await _factory.AddDownloadAsync(productId, ContentVisibility.Entitled);

        using var client = await SignedInAsync("dl-denied");

        var page = await GetEnglishAsync(client, "/products/dl-denied-product");
        var attempt = await client.GetAsync($"/products/dl-denied-product/downloads/{downloadId}");

        // Neither the page nor the refusal carries the destination. Forbid() under cookie
        // authentication is itself a redirect — to the access-denied page — so what matters is
        // where the redirect goes, not that there isn't one.
        Assert.DoesNotContain("downloads.example.com", page, StringComparison.OrdinalIgnoreCase);

        var location = attempt.Headers.Location?.ToString() ?? string.Empty;

        Assert.DoesNotContain("downloads.example.com", location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Account/AccessDenied", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_download_belonging_to_another_product_is_not_reachable_through_this_one()
    {
        // The id is a Guid, so this is not guessable — but the pairing is checked anyway, because
        // an id that leaks from one page must not become access on another.
        var userId = await _factory.CreateMemberAsync("dl-idor");

        var ownProduct = await _factory.CreateProductAsync("dl-idor-own");
        var otherProduct = await _factory.CreateProductAsync("dl-idor-other");

        var otherDownload = await _factory.AddDownloadAsync(otherProduct, ContentVisibility.Public);

        await _factory.GrantAsync(userId, ownProduct);

        using var client = await SignedInAsync("dl-idor");
        var response = await client.GetAsync($"/products/dl-idor-own/downloads/{otherDownload}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Both_a_taken_and_a_refused_download_are_audited()
    {
        var userId = await _factory.CreateMemberAsync("dl-audit");
        var productId = await _factory.CreateProductAsync(
            "dl-audit-product", requiresExplicitEntitlement: true);

        var open = await _factory.AddDownloadAsync(productId, ContentVisibility.Public);
        var closed = await _factory.AddDownloadAsync(productId, ContentVisibility.Entitled);

        using var client = await SignedInAsync("dl-audit");
        await client.GetAsync($"/products/dl-audit-product/downloads/{open}");
        await client.GetAsync($"/products/dl-audit-product/downloads/{closed}");

        var takenActions = await _factory.RecentAuditActionsAsync(open.ToString());
        var refusedActions = await _factory.RecentAuditActionsAsync(closed.ToString());

        Assert.Contains("download.started", takenActions);
        Assert.Contains("download.denied", refusedActions);
        Assert.DoesNotContain("download.started", refusedActions);
        Assert.NotEqual(Guid.Empty, userId);
    }

    // ------------------------------------------------------------------- documentation ----

    [Fact]
    public async Task A_published_public_article_is_readable_and_appears_in_the_index()
    {
        await _factory.CreateMemberAsync("doc-reader");
        var productId = await _factory.CreateProductAsync("doc-reader-product");

        await _factory.AddArticleAsync(
            productId, "getting-started", titleEn: "Getting started", isPublished: true);

        using var client = await SignedInAsync("doc-reader");

        var index = await GetEnglishAsync(client, "/products/doc-reader-product/docs");
        var article = await GetEnglishAsync(client, "/products/doc-reader-product/docs/getting-started");

        Assert.Contains("Getting started", index, StringComparison.Ordinal);
        Assert.Contains("Getting started", article, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unpublished_article_is_indistinguishable_from_one_that_does_not_exist()
    {
        await _factory.CreateMemberAsync("doc-draft");
        var productId = await _factory.CreateProductAsync("doc-draft-product");

        await _factory.AddArticleAsync(productId, "draft-guide", isPublished: false);

        using var client = await SignedInAsync("doc-draft");

        var draft = await client.GetAsync("/products/doc-draft-product/docs/draft-guide");
        var absent = await client.GetAsync("/products/doc-draft-product/docs/no-such-guide");

        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        Assert.Equal(absent.StatusCode, draft.StatusCode);
    }

    [Fact]
    public async Task An_entitled_article_is_hidden_from_a_member_without_access()
    {
        await _factory.CreateMemberAsync("doc-entitled-out");
        var productId = await _factory.CreateProductAsync(
            "doc-entitled-product", requiresExplicitEntitlement: true);

        await _factory.AddArticleAsync(
            productId,
            "entitled-guide",
            titleEn: "Entitled guide",
            isPublished: true,
            visibility: ContentVisibility.Entitled);

        using var client = await SignedInAsync("doc-entitled-out");

        var index = await GetEnglishAsync(client, "/products/doc-entitled-product/docs");
        var article = await client.GetAsync("/products/doc-entitled-product/docs/entitled-guide");

        Assert.DoesNotContain("Entitled guide", index, StringComparison.Ordinal);

        // The article page must agree with the index, or hiding the link is decoration.
        Assert.Equal(HttpStatusCode.NotFound, article.StatusCode);
    }

    [Fact]
    public async Task A_documentation_search_never_surfaces_an_article_the_member_cannot_read()
    {
        var userId = await _factory.CreateMemberAsync("doc-search");
        var productId = await _factory.CreateProductAsync(
            "doc-search-product", requiresExplicitEntitlement: true);

        await _factory.AddArticleAsync(
            productId, "public-note", titleEn: "Peculiar public note", isPublished: true);

        await _factory.AddArticleAsync(
            productId,
            "hidden-note",
            titleEn: "Peculiar hidden note",
            isPublished: true,
            visibility: ContentVisibility.Entitled);

        using var client = await SignedInAsync("doc-search");
        var results = await GetEnglishAsync(client, "/products/doc-search-product/docs?search=Peculiar");

        Assert.Contains("Peculiar public note", results, StringComparison.Ordinal);
        Assert.DoesNotContain("Peculiar hidden note", results, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, userId);
    }

    [Fact]
    public async Task A_search_term_full_of_wildcards_matches_nothing_rather_than_everything()
    {
        // The term is escaped before it reaches LIKE, so "%" is a literal percent sign.
        await _factory.CreateMemberAsync("doc-wildcard");
        var productId = await _factory.CreateProductAsync("doc-wildcard-product");

        await _factory.AddArticleAsync(
            productId, "ordinary", titleEn: "Ordinary article", isPublished: true);

        using var client = await SignedInAsync("doc-wildcard");
        var results = await GetEnglishAsync(client, "/products/doc-wildcard-product/docs?search=%25");

        Assert.DoesNotContain("Ordinary article", results, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previous_and_next_only_ever_point_at_readable_articles()
    {
        await _factory.CreateMemberAsync("doc-nav");
        var productId = await _factory.CreateProductAsync(
            "doc-nav-product", requiresExplicitEntitlement: true);

        await _factory.AddArticleAsync(
            productId, "first", titleEn: "First", isPublished: true, displayOrder: 10);

        // Sits between the two readable ones in order, and must be skipped by the navigation.
        await _factory.AddArticleAsync(
            productId, "middle-hidden", titleEn: "Middle hidden", isPublished: true,
            visibility: ContentVisibility.Entitled, displayOrder: 20);

        await _factory.AddArticleAsync(
            productId, "third", titleEn: "Third", isPublished: true, displayOrder: 30);

        using var client = await SignedInAsync("doc-nav");
        var page = await GetEnglishAsync(client, "/products/doc-nav-product/docs/first");

        Assert.Contains("/docs/third", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/docs/middle-hidden", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_documentation_endpoints_are_closed_to_anonymous_visitors()
    {
        using var client = _factory.CreateNonRedirectingClient();

        foreach (var path in new[]
                 {
                     "/products/anything/docs",
                     "/products/anything/docs/some-slug",
                     $"/products/anything/downloads/{Guid.NewGuid()}",
                 })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(
                "/Account/Login",
                response.Headers.Location?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // -------------------------------------------------------------------------- slugs ----

    [Fact]
    public async Task A_second_article_with_the_same_derived_slug_gets_a_suffix()
    {
        var productId = await _factory.CreateProductAsync("doc-slug-product");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IProductContentAdminService>();

            var first = await admin.SaveArticleAsync(productId, null, Article("Setup guide"));
            var second = await admin.SaveArticleAsync(productId, null, Article("Setup guide"));

            Assert.True(first.Succeeded);
            Assert.True(second.Succeeded);

            var query = services.GetRequiredService<IProductContentAdminQuery>();
            var articles = await query.ListArticlesAsync(productId);

            var slugs = articles.Select(article => article.Slug).OrderBy(slug => slug).ToList();

            Assert.Equal(["setup-guide", "setup-guide-2"], slugs);
        });

        static DocumentationArticleSaveRequest Article(string titleEn) => new(
            CategoryId: null,
            Slug: null,
            TitleFa: "راهنما",
            TitleEn: titleEn,
            SummaryFa: null,
            SummaryEn: null,
            MarkupFa: null,
            MarkupEn: null,
            Visibility: ContentVisibility.Public,
            Platform: null,
            DisplayOrder: 100,
            IsPublished: true,
            ConcurrencyToken: null);
    }

    [Fact]
    public async Task Re_saving_an_article_keeps_its_slug_rather_than_bumping_the_suffix()
    {
        var productId = await _factory.CreateProductAsync("doc-reslug-product");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IProductContentAdminService>();
            var query = services.GetRequiredService<IProductContentAdminQuery>();

            var created = await admin.SaveArticleAsync(productId, null, Request("Install notes"));
            Assert.True(created.Succeeded);

            var article = await query.GetArticleAsync(created.Value);
            Assert.NotNull(article);
            Assert.Equal("install-notes", article!.Slug);

            // Saved again with the slug it already has: uniqueness must exclude the row itself.
            var updated = await admin.SaveArticleAsync(
                productId, created.Value, Request("Install notes") with { Slug = article.Slug });

            Assert.True(updated.Succeeded);

            var after = await query.GetArticleAsync(created.Value);
            Assert.Equal("install-notes", after!.Slug);
        });

        static DocumentationArticleSaveRequest Request(string titleEn) => new(
            CategoryId: null,
            Slug: null,
            TitleFa: "نصب",
            TitleEn: titleEn,
            SummaryFa: null,
            SummaryEn: null,
            MarkupFa: null,
            MarkupEn: null,
            Visibility: ContentVisibility.Public,
            Platform: null,
            DisplayOrder: 100,
            IsPublished: true,
            ConcurrencyToken: null);
    }

    [Fact]
    public async Task A_download_url_that_is_not_https_is_refused_by_the_service()
    {
        var productId = await _factory.CreateProductAsync("dl-policy-product");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IProductContentAdminService>();

            foreach (var url in new[]
                     {
                         "http://downloads.example.com/x.exe",
                         "javascript:alert(1)",
                         "https://user:secret@downloads.example.com/x.exe",
                         "/relative/path",
                     })
            {
                var result = await admin.SaveDownloadAsync(productId, null, new ProductDownloadSaveRequest(
                    DownloadPlatform.Windows,
                    ContentVisibility.Public,
                    "دانلود",
                    "Download",
                    null,
                    null,
                    url,
                    null,
                    null,
                    null,
                    100,
                    true,
                    null));

                Assert.False(result.Succeeded, $"'{url}' should have been refused.");
            }
        });
    }
}
