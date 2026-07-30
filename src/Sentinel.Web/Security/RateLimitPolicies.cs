using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Sentinel.Web.Security;

public static class RateLimitPolicies
{
    public const string Login = "sentinel.login";

    /// <summary>The anonymous subscription-delivery endpoint.</summary>
    public const string Delivery = "sentinel.delivery";

    public static IServiceCollection AddSentinelRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(Login, context =>
            {
                var options = context.RequestServices
                    .GetRequiredService<IOptions<SentinelSecurityOptions>>().Value.LoginRateLimit;

                // Partition by source address. Identity's per-account lockout already covers
                // "many guesses at one account"; this covers the other shape of the attack —
                // one client spraying one password across many accounts, which never trips a
                // per-account counter.
                var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = options.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            });

            // The delivery endpoint is unauthenticated by necessity — a VPN client application cannot
            // sign in — so it is the one surface where an outsider can make the portal do work by
            // simply asking. Each request costs a database lookup and, on a hit, a panel round trip.
            //
            // The limit is generous compared with sign-in: a legitimate client polls its subscription
            // URL every few minutes, and several devices behind one address is normal. What it stops
            // is a client stuck in a retry loop, and anyone walking the token space — though 256 bits
            // of entropy makes that hopeless regardless.
            limiter.AddPolicy(Delivery, context =>
            {
                var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),

                    // No queue: a client application retries on its own schedule, and holding its
                    // request open would tie up a connection to no benefit.
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Sentinel.RateLimit");

                logger.LogWarning(
                    "Rate limit reached for {Path} from {IpAddress}.",
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress);

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync(
                    "Too many attempts. Please wait and try again.", cancellationToken);
            };
        });

        return services;
    }
}
