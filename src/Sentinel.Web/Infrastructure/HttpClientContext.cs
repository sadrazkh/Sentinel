using System.Security.Claims;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Security;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Adapts the current <see cref="HttpContext"/> to <see cref="IClientContext"/> so the
/// application layer can audit who did what, from where, without referencing ASP.NET Core.
/// </summary>
public sealed class HttpClientContext : IClientContext
{
    private const int UserAgentMaxLength = 512;

    private readonly IHttpContextAccessor _accessor;

    public HttpClientContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private HttpContext? Context => _accessor.HttpContext;

    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var raw = Context?.Request.Headers.UserAgent.ToString();

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return raw.Length <= UserAgentMaxLength ? raw : raw[..UserAgentMaxLength];
        }
    }

    public string CorrelationId => Context is null ? string.Empty : CorrelationIdMiddleware.Current(Context);

    public Guid? UserId
    {
        get
        {
            var raw = Context?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? UserName => Context?.User.Identity?.IsAuthenticated == true
        ? Context.User.FindFirstValue(ClaimTypes.Name)
        : null;

    public Guid? SessionId
    {
        get
        {
            var raw = Context?.User.FindFirstValue(UserSession.ClaimType);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }
}
