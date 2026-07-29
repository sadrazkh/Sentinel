using Microsoft.Extensions.Options;

namespace Sentinel.Web.Security;

public sealed class SecurityHeaderOptions
{
    public const string SectionName = "SecurityHeaders";

    /// <summary>
    /// Reports policy violations without enforcing them. Useful when introducing a stricter
    /// policy on an existing deployment; leave off in normal operation.
    /// </summary>
    public bool ContentSecurityPolicyReportOnly { get; set; }

    /// <summary>Optional endpoint that receives CSP violation reports.</summary>
    public string? ContentSecurityPolicyReportUri { get; set; }

    /// <summary>
    /// Adds <c>upgrade-insecure-requests</c>. On by default; turn it off only for a
    /// deployment that is deliberately served over plain HTTP behind a trusted proxy.
    /// </summary>
    public bool UpgradeInsecureRequests { get; set; } = true;
}

/// <summary>
/// Sets the response headers that browsers enforce on our behalf. Applied to every response,
/// including error pages and static files, because a header that only covers the happy path
/// covers nothing.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeaderOptions _options;
    private readonly bool _isDevelopment;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        IOptions<SecurityHeaderOptions> options,
        IWebHostEnvironment environment)
    {
        _next = next;
        _options = options.Value;
        _isDevelopment = environment.IsDevelopment();
    }

    public Task InvokeAsync(HttpContext context)
    {
        var nonce = ContentSecurityPolicyNonce.GetOrCreate(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // frame-ancestors below is the modern control; this covers browsers that still
            // only understand the legacy header.
            headers["X-Frame-Options"] = "DENY";

            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            headers["Permissions-Policy"] =
                "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), " +
                "fullscreen=(self), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), " +
                "midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), " +
                "screen-wake-lock=(), usb=(), xr-spatial-tracking=()";

            var headerName = _options.ContentSecurityPolicyReportOnly
                ? "Content-Security-Policy-Report-Only"
                : "Content-Security-Policy";

            headers[headerName] = BuildPolicy(nonce);

            // Kestrel's Server header is suppressed at startup; strip anything a proxy added.
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
            headers.Remove("X-AspNet-Version");
            headers.Remove("X-AspNetMvc-Version");

            return Task.CompletedTask;
        });

        return _next(context);
    }

    private string BuildPolicy(string nonce)
    {
        var directives = new List<string>
        {
            "default-src 'self'",
            "base-uri 'self'",
            "object-src 'none'",

            // Clickjacking: this portal is never meant to be framed.
            "frame-ancestors 'none'",
            "frame-src 'none'",

            // Forms may only post back to us, which blunts phishing overlays that try to
            // retarget a login form at an attacker's endpoint.
            "form-action 'self'",

            // No CDNs and no inline script. Vue templates are compiled at build time, so
            // 'unsafe-eval' is not needed and is deliberately absent.
            $"script-src 'self' 'nonce-{nonce}'",
            $"style-src 'self' 'nonce-{nonce}'",

            // Style *attributes* only. Razor emits a few (progress widths, brand accents);
            // an injected style attribute cannot execute script.
            "style-src-attr 'unsafe-inline'",

            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",
            "manifest-src 'self'",
            "media-src 'none'",
            "worker-src 'self'",
        };

        if (_options.UpgradeInsecureRequests && !_isDevelopment)
        {
            directives.Add("upgrade-insecure-requests");
        }

        if (!string.IsNullOrWhiteSpace(_options.ContentSecurityPolicyReportUri))
        {
            directives.Add($"report-uri {_options.ContentSecurityPolicyReportUri}");
        }

        return string.Join("; ", directives);
    }
}
