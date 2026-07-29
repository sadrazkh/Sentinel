using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Authorization;
using Sentinel.Application.Catalog;
using Sentinel.Application.Media;

namespace Sentinel.Web.Controllers;

/// <summary>
/// Serves uploaded application icons.
/// <para>
/// Uploads live outside the web root, so they are never handled by the static-file middleware.
/// Everything about the response is decided here: the content type comes from the stored
/// file's own extension — which this application generated — and never from anything the
/// uploader supplied. That, plus the global <c>X-Content-Type-Options: nosniff</c>, is what
/// stops an uploaded file from ever being interpreted as anything but an image.
/// </para>
/// </summary>
[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class MediaController : Controller
{
    /// <summary>
    /// Long, because the URL carries a version derived from the stored file name: replacing an
    /// icon produces a new name and therefore a new URL, so a cached copy can never go stale.
    /// </summary>
    private const int CacheSeconds = 86_400;

    private readonly IApplicationAdminQuery _applications;
    private readonly IApplicationIconStorage _storage;

    public MediaController(IApplicationAdminQuery applications, IApplicationIconStorage storage)
    {
        _applications = applications;
        _storage = storage;
    }

    [HttpGet("/media/app-icon/{key}")]
    public async Task<IActionResult> ApplicationIcon(string key, CancellationToken cancellationToken)
    {
        if (!ApplicationKey.IsValid(key))
        {
            return NotFound();
        }

        var storedName = await _applications.GetIconNameAsync(key, cancellationToken);

        // Re-validated even though this application wrote the value: the row could predate the
        // rule or arrive from a restore, and this is the one place it steers a file read.
        if (!IconFileName.IsValid(storedName))
        {
            return NotFound();
        }

        var stream = await _storage.OpenReadAsync(storedName!, cancellationToken);

        if (stream is null)
        {
            return NotFound();
        }

        var format = IconFileName.FormatOf(storedName!);

        if (format == ImageFormat.Unknown)
        {
            await stream.DisposeAsync();
            return NotFound();
        }

        // Private: these sit behind authentication and must not be held by a shared cache.
        Response.Headers.CacheControl = $"private, max-age={CacheSeconds}";

        // No file name is offered. Inline display is the only intended use, and an attachment
        // name would be one more attacker-influenced string in a response header.
        return File(stream, ImageSignature.ContentTypeFor(format));
    }
}
