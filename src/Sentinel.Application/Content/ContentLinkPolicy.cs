using Sentinel.Application.Catalog;

namespace Sentinel.Application.Content;

/// <summary>
/// Which link targets may appear inside operator-written content.
/// <para>
/// Stricter than <see cref="ApplicationUrlPolicy"/> in one way and looser in another: it also
/// accepts a portal-relative path, because a documentation article legitimately links to another
/// page of the portal, but it refuses the loopback exception — plain http to localhost is useful
/// for a developer's launch URL and meaningless in prose a customer reads.
/// </para>
/// </summary>
public static class ContentLinkPolicy
{
    public const int MaxLength = 2048;

    public static bool IsAllowed(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > MaxLength)
        {
            return false;
        }

        var candidate = target.Trim();

        // Browsers strip tab, CR and LF out of a URL before parsing it, so a raw one is not the
        // inert character it looks like: "/<tab>/evil.example" arrives at the parser as
        // "//evil.example", which is protocol-relative and therefore external. Refused outright
        // rather than stripped, because anything needing them can percent-encode them.
        if (candidate.Any(character => character is '\t' or '\r' or '\n' || char.IsControl(character)))
        {
            return false;
        }

        // A portal-relative path. Required to start with a single slash and to carry no scheme,
        // so "//evil.example" and "/\evil.example" — both of which browsers read as
        // protocol-relative or scheme-relative external URLs — are refused.
        if (candidate.StartsWith('/'))
        {
            return candidate.Length > 1
                   && candidate[1] != '/'
                   && candidate[1] != '\\'
                   && !candidate.Contains(':', StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // mailto is allowed because a support address is a reasonable thing to link. Everything
        // else that is not https is refused: javascript:, data:, file:, ftp:, and plain http,
        // which would downgrade a member who followed it.
        if (uri.Scheme == Uri.UriSchemeMailto)
        {
            // .NET splits a mailto address across UserInfo and Host and leaves AbsolutePath
            // empty, so the address has to be read from those two parts rather than the path.
            return !string.IsNullOrEmpty(uri.UserInfo)
                   && !string.IsNullOrEmpty(uri.Host)
                   && uri.Host.Contains('.', StringComparison.Ordinal);
        }

        return uri.Scheme == Uri.UriSchemeHttps
               && !string.IsNullOrEmpty(uri.Host)
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}
