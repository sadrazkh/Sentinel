using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Authorization;
using Sentinel.Application.Catalog;
using Sentinel.Application.Media;
using Sentinel.Application.Products;

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
    private readonly IProductContentService _content;

    // Named for its first use, but the store itself is generic: a flat directory of
    // server-generated names. Documentation step images live in it alongside product icons.
    private readonly IApplicationIconStorage _storage;

    public MediaController(
        IApplicationAdminQuery applications,
        IProductContentService content,
        IApplicationIconStorage storage)
    {
        _applications = applications;
        _content = content;
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

    /// <summary>
    /// Serves the screenshot attached to one documentation step.
    /// <para>
    /// Routed through the content service rather than reading the step row directly, so the
    /// image is behind exactly the same audience check as the article that shows it. An image
    /// reachable without that check would be a way to read entitled content by URL.
    /// </para>
    /// </summary>
    [HttpGet("/media/doc-step/{key}/{slug}/{step:int}")]
    public async Task<IActionResult> StepImage(
        string key,
        string slug,
        int step,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Forbid();
        }

        var article = await _content.GetArticleAsync(userId, key, slug, cancellationToken);

        if (article is null)
        {
            return NotFound();
        }

        var storedName = article.Steps
            .FirstOrDefault(candidate => candidate.StepNumber == step)?.MediaPath;

        // Re-validated even though this application wrote the value: the row could predate the
        // rule or arrive from a restore, and this is the one place it steers a file read.
        if (!IconFileName.IsValid(storedName))
        {
            return NotFound();
        }

        var format = IconFileName.FormatOf(storedName!);

        if (format == ImageFormat.Unknown)
        {
            return NotFound();
        }

        var stream = await _storage.OpenReadAsync(storedName!, cancellationToken);

        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = $"private, max-age={CacheSeconds}";

        return File(stream, ImageSignature.ContentTypeFor(format));
    }
}
