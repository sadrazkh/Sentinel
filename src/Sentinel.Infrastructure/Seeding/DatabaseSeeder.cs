using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Seeding;

/// <summary>
/// Idempotent first-boot seeding: roles, the initial SuperAdmin, and — only when explicitly
/// enabled — a sample catalogue for local development.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly SentinelDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly SeedOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatabaseSeeder> _logger;

    private static readonly (string Name, string Description)[] Roles =
    [
        (RoleNames.SuperAdmin, "Full control, including role assignment and system settings."),
        (RoleNames.Admin, "Manages users, memberships, applications and entitlements."),
        (RoleNames.Support, "Read-only access to user and audit data for troubleshooting."),
        (RoleNames.Member, "An ordinary portal customer."),
    ];

    public DatabaseSeeder(
        SentinelDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<SeedOptions> options,
        TimeProvider timeProvider,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync(cancellationToken);
        await SeedSuperAdminAsync(cancellationToken);

        if (_options.IncludeSampleApplications)
        {
            await SeedSampleApplicationsAsync(cancellationToken);
        }
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, description) in Roles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _roleManager.RoleExistsAsync(name))
            {
                continue;
            }

            var result = await _roleManager.CreateAsync(new ApplicationRole(name, description)
            {
                Id = SequentialGuid.New(),
            });

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create role '{name}': {Describe(result)}");
            }

            _logger.LogInformation("Created role {Role}.", name);
        }
    }

    private async Task SeedSuperAdminAsync(CancellationToken cancellationToken)
    {
        if (!_options.SuperAdmin.Enabled)
        {
            return;
        }

        // Never overwrite or reset an administrator that already exists: seeding may only
        // ever bootstrap the very first one.
        var alreadyExists = await _db.UserRoles
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .AnyAsync(name => name == RoleNames.SuperAdmin, cancellationToken);

        if (alreadyExists)
        {
            _logger.LogInformation("A SuperAdmin already exists; skipping seed.");
            return;
        }

        var seed = _options.SuperAdmin;

        if (string.IsNullOrWhiteSpace(seed.UserName)
            || string.IsNullOrWhiteSpace(seed.Email)
            || string.IsNullOrWhiteSpace(seed.Password))
        {
            throw new InvalidOperationException(
                "Seed:SuperAdmin:Enabled is true but UserName, Email or Password is missing. " +
                "Supply the password through the Seed__SuperAdmin__Password environment variable.");
        }

        var now = _timeProvider.GetUtcNow();

        var user = new ApplicationUser
        {
            Id = SequentialGuid.New(now),
            UserName = seed.UserName,
            Email = seed.Email,
            EmailConfirmed = true,
            DisplayName = seed.DisplayName,
            Status = UserAccountStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // CreateAsync hashes the password with the configured Identity hasher; the plaintext
        // never reaches the database, a log sink or an audit row.
        var created = await _userManager.CreateAsync(user, seed.Password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create the initial SuperAdmin: {Describe(created)}");
        }

        var assigned = await _userManager.AddToRoleAsync(user, RoleNames.SuperAdmin);
        if (!assigned.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not assign the SuperAdmin role: {Describe(assigned)}");
        }

        _logger.LogWarning(
            "Seeded the initial SuperAdmin '{UserName}'. Sign in, change the password, then set " +
            "Seed:SuperAdmin:Enabled to false and clear Seed__SuperAdmin__Password.",
            seed.UserName);
    }

    private async Task SeedSampleApplicationsAsync(CancellationToken cancellationToken)
    {
        if (await _db.PortalApplications.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // example.com is the IANA-reserved documentation domain: these rows are obviously
        // placeholders and cannot accidentally point at somebody's live service.
        var samples = new List<PortalApplication>
        {
            new()
            {
                Key = "vault",
                NameFa = "والت اسناد",
                NameEn = "Document Vault",
                DescriptionFa = "نگهداری و اشتراک امن فایل‌ها و اسناد سازمانی.",
                DescriptionEn = "Store and share organisational files and documents securely.",
                LaunchUrl = "https://example.com/vault",
                PublishStatus = ApplicationPublishStatus.Published,
                IsEnabled = true,
                DisplayOrder = 10,
                RequiresExplicitEntitlement = false,
            },
            new()
            {
                Key = "analytics",
                NameFa = "تحلیل داده",
                NameEn = "Analytics",
                DescriptionFa = "گزارش‌های زنده و داشبوردهای تحلیلی سرویس شما.",
                DescriptionEn = "Live reports and analytics dashboards for your service.",
                LaunchUrl = "https://example.com/analytics",
                PublishStatus = ApplicationPublishStatus.Published,
                IsEnabled = true,
                IsBeta = true,
                DisplayOrder = 20,
                RequiresExplicitEntitlement = false,
                MinimumTier = MembershipTier.Pro,
            },
            new()
            {
                Key = "automation",
                NameFa = "اتوماسیون",
                NameEn = "Automation Studio",
                DescriptionFa = "ساخت جریان‌های کاری خودکار بدون نیاز به کدنویسی.",
                DescriptionEn = "Build automated workflows without writing code.",
                LaunchUrl = "https://example.com/automation",
                PublishStatus = ApplicationPublishStatus.Published,
                IsEnabled = true,
                DisplayOrder = 30,
                RequiresExplicitEntitlement = true,
            },
            new()
            {
                Key = "insights",
                NameFa = "دستیار هوشمند",
                NameEn = "Insights Assistant",
                DescriptionFa = "به‌زودی: دستیار هوشمند برای تحلیل و پیشنهاد.",
                DescriptionEn = "Coming soon: an assistant for analysis and recommendations.",
                LaunchUrl = "https://example.com/insights",
                PublishStatus = ApplicationPublishStatus.ComingSoon,
                IsEnabled = true,
                DisplayOrder = 40,
                RequiresExplicitEntitlement = false,
            },
        };

        foreach (var sample in samples)
        {
            sample.Id = SequentialGuid.New(now);
            sample.CreatedAt = now;
            sample.UpdatedAt = now;
        }

        _db.PortalApplications.AddRange(samples);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Seeded {Count} sample applications.", samples.Count);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
}
