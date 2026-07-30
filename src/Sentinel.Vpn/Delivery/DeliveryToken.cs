using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Sentinel.Vpn.Delivery;

/// <summary>
/// The token in a service's delivery URL.
/// <para>
/// This URL is a <b>capability</b>: whoever holds it gets the member's configurations without
/// signing in. That is not a compromise, it is a requirement — a VPN client application polls a
/// subscription URL and has no way to authenticate. So the token has to carry the whole security
/// weight on its own:
/// </para>
/// <list type="bullet">
/// <item><b>32 bytes from a cryptographic source.</b> 256 bits of entropy, so guessing is not a
/// strategy. Derived from nothing — not the service id, not the member, not a counter — because a
/// derivable token is a token an attacker can compute rather than guess.</item>
/// <item><b>Stored as a SHA-256 hash.</b> A database leak must not hand out working configurations.
/// The plaintext exists once, at issue, and is never readable again.</item>
/// <item><b>Rotatable.</b> Issuing a new one invalidates the old immediately, which is the only
/// remedy once a URL has leaked.</item>
/// </list>
/// <para>
/// The hash is unsalted and uses a plain digest rather than a password hash, deliberately: the input
/// is 256 bits of uniform randomness, so there is no dictionary to attack and nothing for a work
/// factor to defend against — while every request from every client application has to verify one.
/// </para>
/// </summary>
public static partial class DeliveryToken
{
    /// <summary>32 bytes, base64url-encoded without padding, so it is 43 characters.</summary>
    public const int ByteLength = 32;

    public const int EncodedLength = 43;

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// Mints a token. The plaintext is returned once and never stored; the caller persists the hash.
    /// </summary>
    public static (string Token, string Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(ByteLength);

        // base64url: safe in a path segment with no escaping, which matters because this value is
        // pasted into client applications by hand.
        var token = Base64UrlEncode(bytes);

        return (token, Hash(token));
    }

    /// <summary>
    /// Whether a value has the shape this class mints.
    /// <para>
    /// Checked before hashing so a malformed request is refused without doing the work, and so
    /// nothing odd reaches a database lookup.
    /// </para>
    /// </summary>
    public static bool IsWellFormed(string? token) =>
        !string.IsNullOrEmpty(token) && Pattern().IsMatch(token);

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// The first few characters, for a log line or an audit row.
    /// <para>
    /// A prefix rather than the whole thing: enough to correlate two log entries about the same
    /// request, not enough to be the credential. Eight characters of base64url is 48 bits — far too
    /// little to guess the remaining 208.
    /// </para>
    /// </summary>
    public static string Fingerprint(string? token) =>
        string.IsNullOrEmpty(token) || token.Length < 8 ? "?" : token[..8] + "…";

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
