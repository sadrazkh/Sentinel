namespace Sentinel.Infrastructure.Seeding;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public SuperAdminSeedOptions SuperAdmin { get; set; } = new();

    /// <summary>
    /// Adds a small catalogue of demonstration applications so a fresh install has something
    /// to show. Off by default; intended for local development only.
    /// </summary>
    public bool IncludeSampleApplications { get; set; }
}

public sealed class SuperAdminSeedOptions
{
    /// <summary>
    /// Creates the first SuperAdmin when none exists. Turn it on for the initial boot only.
    /// It is a no-op if any SuperAdmin is already present, so leaving it on cannot be used to
    /// silently overwrite an existing administrator.
    /// </summary>
    public bool Enabled { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = "System Administrator";

    /// <summary>
    /// Supplied only through an environment variable or secret store
    /// (<c>Seed__SuperAdmin__Password</c>). It is never written to configuration files, never
    /// logged, and is discarded from memory once Identity has hashed it.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
