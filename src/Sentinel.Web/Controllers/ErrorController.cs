using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models;

namespace Sentinel.Web.Controllers;

[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorController : Controller
{
    private readonly ILogger<ErrorController> _logger;

    public ErrorController(ILogger<ErrorController> logger) => _logger = logger;

    /// <summary>
    /// Terminal handler for unhandled exceptions. The exception is logged in full with the
    /// correlation id; the response carries the id and nothing else.
    /// </summary>
    [Route("/error")]
    public IActionResult Index()
    {
        var correlationId = CorrelationIdMiddleware.Current(HttpContext);
        var feature = HttpContext.Features.Get<IExceptionHandlerFeature>();

        if (feature?.Error is { } exception)
        {
            _logger.LogError(
                exception,
                "Unhandled exception for {Method} {Path} (correlation {CorrelationId}).",
                HttpContext.Request.Method,
                feature.Path,
                correlationId);
        }

        Response.StatusCode = StatusCodes.Status500InternalServerError;

        return View("Error", new ErrorViewModel
        {
            CorrelationId = correlationId,
            StatusCode = StatusCodes.Status500InternalServerError,
        });
    }

    /// <summary>Renders a branded page for status codes produced without an exception (404, 403, 429…).</summary>
    [Route("/error/{statusCode:int:range(400,599)}")]
    public IActionResult StatusCodeHandler(int statusCode)
    {
        var (titleKey, messageKey) = statusCode switch
        {
            StatusCodes.Status400BadRequest => ("error.400.title", "error.400.message"),
            StatusCodes.Status403Forbidden => ("error.403.title", "error.403.message"),
            StatusCodes.Status404NotFound => ("error.404.title", "error.404.message"),
            StatusCodes.Status429TooManyRequests => ("error.429.title", "error.429.message"),
            StatusCodes.Status503ServiceUnavailable => ("error.503.title", "error.503.message"),
            _ => ("error.500.title", "error.500.message"),
        };

        Response.StatusCode = statusCode;

        return View("Error", new ErrorViewModel
        {
            CorrelationId = CorrelationIdMiddleware.Current(HttpContext),
            StatusCode = statusCode,
            TitleKey = titleKey,
            MessageKey = messageKey,
        });
    }
}
