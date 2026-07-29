using Serilog.Context;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Gives every request an id that appears in the logs, in audit rows and on the error page,
/// so a user can quote one string and an operator can find the exact failure.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "sentinel:correlation-id";

    private const int MaxLength = 64;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveFromRequest(context) ?? Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// An inbound id is honoured so a trace can span services, but only after strict
    /// validation: it is echoed into response headers and log lines, and an unvalidated value
    /// would let a client forge log entries or smuggle characters into a header.
    /// </summary>
    private static string? ResolveFromRequest(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return null;
        }

        var candidate = values.ToString();

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxLength)
        {
            return null;
        }

        foreach (var c in candidate)
        {
            var allowed = char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
            if (!allowed)
            {
                return null;
            }
        }

        return candidate;
    }

    public static string Current(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id
            ? id
            : string.Empty;
}
