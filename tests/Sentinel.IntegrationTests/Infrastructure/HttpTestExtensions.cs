using System.Net;
using System.Text.RegularExpressions;

namespace Sentinel.IntegrationTests.Infrastructure;

public static partial class HttpTestExtensions
{
    [GeneratedRegex(
        """<input name="__RequestVerificationToken" type="hidden" value="([^"]+)" />""",
        RegexOptions.IgnoreCase)]
    private static partial Regex AntiForgeryFieldRegex();

    /// <summary>
    /// Fetches a page and pulls out its anti-forgery field. The matching cookie is captured
    /// automatically by the client's cookie container, so a caller that uses the same client
    /// ends up with the pair the server expects.
    /// </summary>
    public static async Task<string> GetAntiForgeryTokenAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var match = AntiForgeryFieldRegex().Match(html);

        Assert.True(match.Success, $"No anti-forgery field found in the response from {url}.");
        return match.Groups[1].Value;
    }

    public static Task<HttpResponseMessage> PostLoginAsync(
        this HttpClient client,
        string token,
        string identifier,
        string password,
        string? returnUrl = null,
        bool rememberMe = false)
    {
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Identifier"] = identifier,
            ["Password"] = password,
            ["RememberMe"] = rememberMe ? "true" : "false",
        };

        if (returnUrl is not null)
        {
            fields["ReturnUrl"] = returnUrl;
        }

        return client.PostAsync("/Account/Login", new FormUrlEncodedContent(fields));
    }

    /// <summary>Signs the client in and asserts it worked, for tests whose subject is elsewhere.</summary>
    public static async Task SignInAsync(this HttpClient client, string identifier, string password)
    {
        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(token, identifier, password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public static IReadOnlyList<string> SetCookies(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    /// <summary>
    /// Returns the live authentication cookie as a <c>name=value</c> pair.
    /// <para>
    /// A successful sign-in emits <em>two</em> Set-Cookie headers for the auth cookie: an
    /// expired one from the deliberate sign-out that defeats session fixation, and then the
    /// newly issued one. Taking the first would hand back the deletion.
    /// </para>
    /// </summary>
    public static string? FindAuthCookie(this HttpResponseMessage response) =>
        response.SetCookies()
            .Select(value => value.Split(';')[0])
            .LastOrDefault(pair =>
                pair.StartsWith("sentinel.auth=", StringComparison.Ordinal)
                && pair.Length > "sentinel.auth=".Length);

    public static bool IssuedAnAuthCookie(this HttpResponseMessage response) =>
        response.FindAuthCookie() is not null;
}
