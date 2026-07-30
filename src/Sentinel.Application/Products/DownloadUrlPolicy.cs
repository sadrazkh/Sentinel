namespace Sentinel.Application.Products;

/// <summary>
/// What may be stored as a download destination.
/// <para>
/// HTTPS only, with no loopback exception. A download is a file a member is told to run: served
/// over plain http it can be swapped in transit by anyone on the path, which is a worse outcome
/// than a page being read. That makes this stricter than the launch-URL policy, which tolerates
/// http to localhost so a developer can point an entry at their own machine.
/// </para>
/// </summary>
public static class DownloadUrlPolicy
{
    public const int MaxLength = 2048;

    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength)
        {
            return false;
        }

        // Browsers strip raw tab, CR and LF out of a URL before parsing, so one hidden in the
        // middle changes where the link actually goes.
        if (url.Any(character => char.IsControl(character)))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
               && !string.IsNullOrEmpty(uri.Host)
               // A credential in the URL would be handed to the browser and land in history.
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}
