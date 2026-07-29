using System.ComponentModel.DataAnnotations;

namespace Sentinel.Web.Security;

public sealed class SentinelSecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Forces HTTPS redirection, HSTS and <c>Secure</c> cookies, and switches the
    /// authentication cookie to the <c>__Host-</c> prefix. Only ever turned off for the
    /// in-memory integration test host, which speaks plain HTTP to a loopback address.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    [Range(5, 60 * 24 * 30)]
    public int SessionLifetimeMinutes { get; set; } = 480;

    /// <summary>Extends an active session on use, up to <see cref="SessionLifetimeMinutes"/> again.</summary>
    public bool SlidingExpiration { get; set; } = true;

    /// <summary>Trusted reverse proxy hops for X-Forwarded-* handling. 0 disables the feature.</summary>
    [Range(0, 8)]
    public int ForwardedHeaderHops { get; set; }

    public PasswordPolicyOptions Password { get; set; } = new();

    public LockoutPolicyOptions Lockout { get; set; } = new();

    public LoginRateLimitOptions LoginRateLimit { get; set; } = new();
}

public sealed class PasswordPolicyOptions
{
    /// <summary>
    /// Length is the property that actually resists guessing, so the floor is high and the
    /// character-class rules stay light — piling on classes mostly produces "Password1!".
    /// </summary>
    [Range(8, 128)]
    public int MinimumLength { get; set; } = 12;

    [Range(1, 32)]
    public int RequiredUniqueChars { get; set; } = 5;

    public bool RequireDigit { get; set; } = true;

    public bool RequireLowercase { get; set; } = true;

    public bool RequireUppercase { get; set; }

    public bool RequireNonAlphanumeric { get; set; }
}

public sealed class LockoutPolicyOptions
{
    [Range(3, 20)]
    public int MaxFailedAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;
}

public sealed class LoginRateLimitOptions
{
    /// <summary>Sign-in attempts allowed per window from one source address.</summary>
    [Range(1, 1000)]
    public int PermitLimit { get; set; } = 10;

    [Range(5, 3600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Requests beyond the limit are rejected outright rather than queued: making a
    /// brute-force client wait in a queue would still let it consume server capacity.
    /// </summary>
    [Range(0, 100)]
    public int QueueLimit { get; set; }
}
