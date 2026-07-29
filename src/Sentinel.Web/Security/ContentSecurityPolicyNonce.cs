using System.Security.Cryptography;

namespace Sentinel.Web.Security;

/// <summary>
/// Per-request nonce that lets the handful of unavoidable inline scripts run under a CSP that
/// otherwise forbids inline script entirely. Regenerated for every response — a reused nonce
/// would be as good as <c>unsafe-inline</c> to an attacker who can read one page.
/// </summary>
public static class ContentSecurityPolicyNonce
{
    private const string ItemKey = "sentinel:csp-nonce";
    private const int ByteLength = 16;

    public static string GetOrCreate(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is string nonce)
        {
            return nonce;
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ByteLength));
        context.Items[ItemKey] = generated;
        return generated;
    }
}
