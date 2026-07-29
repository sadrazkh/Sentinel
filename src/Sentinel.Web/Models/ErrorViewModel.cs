namespace Sentinel.Web.Models;

/// <summary>
/// Everything the error page is allowed to show. Exception types, messages and stack traces
/// are deliberately absent: the correlation id is the only thing the user needs in order for
/// an operator to find the full detail in the logs.
/// </summary>
public sealed class ErrorViewModel
{
    public required string CorrelationId { get; init; }

    public int StatusCode { get; init; } = StatusCodes.Status500InternalServerError;

    /// <summary>Localisation key for the headline, chosen from the status code.</summary>
    public string TitleKey { get; init; } = "error.500.title";

    public string MessageKey { get; init; } = "error.500.message";
}
