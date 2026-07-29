namespace Sentinel.Application.Abstractions;

/// <summary>
/// Request-scoped facts the application layer needs for auditing without taking a
/// dependency on ASP.NET Core's <c>HttpContext</c>.
/// </summary>
public interface IClientContext
{
    string? IpAddress { get; }

    string? UserAgent { get; }

    /// <summary>Identifier echoed to the user on error pages and attached to every log line.</summary>
    string CorrelationId { get; }

    Guid? UserId { get; }

    string? UserName { get; }

    /// <summary>Id of the <c>UserSession</c> row backing the current authentication cookie.</summary>
    Guid? SessionId { get; }
}
