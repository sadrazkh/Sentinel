using Sentinel.Application.Media;

namespace Sentinel.Web.Infrastructure;

public static class MediaUrls
{
    /// <summary>
    /// URL for an application's icon, or <c>null</c> when it has none.
    /// <para>
    /// The version segment is taken from the stored file name, which changes on every upload.
    /// That is what lets the response be cached for a day without a replaced icon ever being
    /// served stale.
    /// </para>
    /// </summary>
    public static string? ApplicationIcon(string applicationKey, string? storedIconName)
    {
        if (!IconFileName.IsValid(storedIconName))
        {
            return null;
        }

        return $"/media/app-icon/{applicationKey}?v={storedIconName![..8]}";
    }
}
