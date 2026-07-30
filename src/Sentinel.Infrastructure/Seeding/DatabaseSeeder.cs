using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Identity;
using Sentinel.Application.Products;
using Sentinel.Domain.Products;
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
    private readonly IProductContentAdminService _content;
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
        IProductContentAdminService content,
        IOptions<SeedOptions> options,
        TimeProvider timeProvider,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _content = content;
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
            await SeedSampleContentAsync(cancellationToken);
            await SeedSampleMembersAsync(cancellationToken);
            await SeedSampleMembershipsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Creates one account per interesting portal state — healthy, expiring, in grace, expired,
    /// suspended, no membership — so every branch of the access rules can be seen in a browser
    /// rather than inferred from tests. Skipped entirely when no fixture password is configured.
    /// </summary>
    private async Task SeedSampleMembersAsync(CancellationToken cancellationToken)
    {
        var password = _options.SampleMemberPassword;

        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation(
                "Seed:SampleMemberPassword is empty; skipping the sample member accounts.");
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var created = 0;

        foreach (var sample in SampleMembers.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _userManager.FindByNameAsync(sample.UserName) is not null)
            {
                continue;
            }

            var user = new ApplicationUser
            {
                Id = SequentialGuid.New(now),
                UserName = sample.UserName,
                Email = $"{sample.UserName}@example.com",
                EmailConfirmed = true,
                PhoneNumber = sample.PhoneNumber,
                NormalizedPhoneNumber = PhoneNumberNormalizer.Normalize(sample.PhoneNumber),
                DisplayName = sample.DisplayName,
                Status = sample.AccountStatus,
                StatusNote = sample.AccountStatus == UserAccountStatus.Active ? null : sample.Purpose,
            };

            var result = await _userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create the sample member '{sample.UserName}': {Describe(result)}");
            }

            await _userManager.AddToRoleAsync(user, RoleNames.Member);

            if (sample.WithMembership)
            {
                _db.Memberships.Add(new Membership
                {
                    Id = SequentialGuid.New(now),
                    UserId = user.Id,
                    Tier = sample.Tier,
                    AdminState = sample.MembershipState,
                    StartsAt = now.AddDays(-60),
                    EndsAt = sample.EndsInDays is { } days ? now.AddDays(days) : null,
                    Notes = sample.Purpose,
                });
            }

            created++;
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Seeded {Count} sample member account(s) with a shared development password. " +
                "This only ever happens when Seed:IncludeSampleApplications is on, which " +
                "Production refuses.",
                created);
        }
    }

    /// <summary>
    /// Development convenience for accounts that are not part of the sample set — the seeded
    /// administrator, most importantly. Without a membership every application shows as locked,
    /// which makes the portal impossible to look at on a fresh database.
    /// <para>
    /// Sample members are excluded: their membership state is the whole point of them, and
    /// giving <c>member.nomembership</c> a membership would defeat it.
    /// </para>
    /// </summary>
    private async Task SeedSampleMembershipsAsync(CancellationToken cancellationToken)
    {
        var sampleUserNames = SampleMembers.All.Select(s => s.UserName).ToList();

        var usersWithoutMembership = await _db.Users
            .Where(u => u.Membership == null && !sampleUserNames.Contains(u.UserName!))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (usersWithoutMembership.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var userId in usersWithoutMembership)
        {
            _db.Memberships.Add(new Membership
            {
                Id = SequentialGuid.New(now),
                UserId = userId,
                Tier = MembershipTier.Pro,
                AdminState = MembershipAdminState.Active,
                StartsAt = now.AddDays(-30),
                EndsAt = now.AddDays(30),
                Notes = "Seeded for local development.",
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded a development membership for {Count} account(s).", usersWithoutMembership.Count);
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

    /// <summary>
    /// The category set is seeded whenever it is empty, independently of the sample products:
    /// categories are structure an operator organises a real catalogue with, not fixture data.
    /// </summary>
    private async Task<Dictionary<string, Guid>> SeedProductCategoriesAsync(
        CancellationToken cancellationToken)
    {
        var existing = await _db.ProductCategories
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Key, c => c.Id, cancellationToken);

        var now = _timeProvider.GetUtcNow();

        var defaults = new (string Key, string NameFa, string NameEn, int Order)[]
        {
            ("connectivity", "اتصال و شبکه", "Connectivity", 10),
            ("productivity", "بهره‌وری", "Productivity", 20),
            ("tools", "ابزارها", "Tools", 30),
            ("entertainment", "سرگرمی", "Entertainment", 40),
        };

        var created = new List<ProductCategory>();

        foreach (var (key, nameFa, nameEn, order) in defaults)
        {
            if (existing.ContainsKey(key))
            {
                continue;
            }

            var category = new ProductCategory
            {
                Id = SequentialGuid.New(now),
                Key = key,
                NameFa = nameFa,
                NameEn = nameEn,
                DisplayOrder = order,
                IsVisible = true,
                CreatedAt = now,
                UpdatedAt = now,
            };

            created.Add(category);
            existing[key] = category.Id;
        }

        if (created.Count > 0)
        {
            _db.ProductCategories.AddRange(created);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Seeded {Count} product categories.", created.Count);
        }

        return existing;
    }

    private async Task SeedSampleApplicationsAsync(CancellationToken cancellationToken)
    {
        var categories = await SeedProductCategoriesAsync(cancellationToken);

        if (await _db.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // Capabilities are set explicitly rather than defaulted, because they are what the
        // library reads to decide which button a card leads with. Only Launchable is used here:
        // downloads and plans arrive with their own phases, and a capability without the
        // machinery behind it would produce a button that goes nowhere.
        var launchable = ProductCapability.Launchable | ProductCapability.HasDocumentation;

        // example.com is the IANA-reserved documentation domain: these rows are obviously
        // placeholders and cannot accidentally point at somebody's live service.
        var samples = new List<Product>
        {
            new()
            {
                Key = "vault",
                NameFa = "والت اسناد",
                NameEn = "Document Vault",
                SummaryFa = "نگهداری امن اسناد سازمانی.",
                SummaryEn = "Secure storage for organisational documents.",
                DescriptionFa = "نگهداری و اشتراک امن فایل‌ها و اسناد سازمانی.",
                DescriptionEn = "Store and share organisational files and documents securely.",
                LaunchUrl = "https://example.com/vault",
                Type = ProductType.WebApplication,
                Capabilities = launchable,
                CategoryId = categories.GetValueOrDefault("productivity"),
                CurrentVersion = "1.4.0",
                ReleaseStatus = ProductReleaseStatus.Stable,
                IsEnabled = true,
                IsFeatured = true,
                DisplayOrder = 10,
                RequiresExplicitEntitlement = false,
            },
            new()
            {
                Key = "analytics",
                NameFa = "تحلیل داده",
                NameEn = "Analytics",
                SummaryFa = "داشبوردهای تحلیلی زنده.",
                SummaryEn = "Live analytics dashboards.",
                DescriptionFa = "گزارش‌های زنده و داشبوردهای تحلیلی سرویس شما.",
                DescriptionEn = "Live reports and analytics dashboards for your service.",
                LaunchUrl = "https://example.com/analytics",
                Type = ProductType.WebApplication,
                Capabilities = launchable,
                CategoryId = categories.GetValueOrDefault("productivity"),
                CurrentVersion = "2.0.1",
                ReleaseStatus = ProductReleaseStatus.Stable,
                IsEnabled = true,
                DisplayOrder = 20,
                RequiresExplicitEntitlement = false,
                MinimumTier = MembershipTier.Pro,
            },
            new()
            {
                Key = "automation",
                NameFa = "اتوماسیون",
                NameEn = "Automation Studio",
                SummaryFa = "ساخت جریان کاری بدون کدنویسی.",
                SummaryEn = "Build workflows without code.",
                DescriptionFa = "ساخت جریان‌های کاری خودکار بدون نیاز به کدنویسی.",
                DescriptionEn = "Build automated workflows without writing code.",
                LaunchUrl = "https://example.com/automation",
                Type = ProductType.WebApplication,
                Capabilities = launchable,
                CategoryId = categories.GetValueOrDefault("tools"),
                ReleaseStatus = ProductReleaseStatus.Stable,
                IsEnabled = true,
                DisplayOrder = 30,
                RequiresExplicitEntitlement = true,
            },
            new()
            {
                Key = "insights",
                NameFa = "دستیار هوشمند",
                NameEn = "Insights Assistant",
                SummaryFa = "به‌زودی در دسترس قرار می‌گیرد.",
                SummaryEn = "Arriving soon.",
                DescriptionFa = "به‌زودی: دستیار هوشمند برای تحلیل و پیشنهاد.",
                DescriptionEn = "Coming soon: an assistant for analysis and recommendations.",
                LaunchUrl = "https://example.com/insights",
                Type = ProductType.WebApplication,
                Capabilities = launchable,
                CategoryId = categories.GetValueOrDefault("tools"),
                ReleaseStatus = ProductReleaseStatus.ComingSoon,
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

        _db.Products.AddRange(samples);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Seeded {Count} sample products.", samples.Count);
    }

    /// <summary>
    /// A page section, a download and a two-step guide for the vault sample, so the content
    /// surfaces are visible in a browser on a fresh database rather than only in tests.
    /// <para>
    /// Routed through <see cref="IProductContentAdminService"/> rather than writing rows, so the
    /// seeded HTML is produced by the same renderer a real edit goes through.
    /// </para>
    /// </summary>
    private async Task SeedSampleContentAsync(CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Where(p => p.Key == "vault")
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null || await _db.ProductSections.AnyAsync(cancellationToken))
        {
            return;
        }

        await _content.SaveSectionAsync(
            product.Id,
            null,
            new ProductSectionSaveRequest(
                ProductSectionKind.Features,
                ContentVisibility.Public,
                "ویژگی‌ها",
                "Features",
                "- نگهداری رمزنگاری‌شده\n- اشتراک‌گذاری با لینک منقضی‌شدنی\n- تاریخچهٔ نسخه‌ها",
                "- Encrypted storage\n- Sharing by expiring link\n- Version history",
                10,
                true,
                null),
            cancellationToken);

        await _content.SaveSectionAsync(
            product.Id,
            null,
            new ProductSectionSaveRequest(
                ProductSectionKind.Requirements,
                ContentVisibility.Entitled,
                "پیش‌نیازها",
                "Requirements",
                "برای اتصال به میزبان `vault.example.com` نیاز به **دسترسی فعال** دارید.",
                "Connecting to `vault.example.com` needs **active access**.",
                20,
                true,
                null),
            cancellationToken);

        await _content.SaveDownloadAsync(
            product.Id,
            null,
            new ProductDownloadSaveRequest(
                DownloadPlatform.Windows,
                ContentVisibility.Entitled,
                "کلاینت ویندوز",
                "Windows client",
                null,
                null,
                // example.com is the IANA-reserved documentation domain, so this placeholder
                // cannot point at somebody's real file.
                "https://downloads.example.com/vault-setup.exe",
                "1.4.0",
                null,
                48_234_496,
                10,
                true,
                null),
            cancellationToken);

        var categoryId = await _content.SaveCategoryAsync(
            product.Id,
            null,
            new DocumentationCategorySaveRequest(
                "getting-started", "شروع کار", "Getting started", null, 10, true, null),
            cancellationToken);

        var articleId = await _content.SaveArticleAsync(
            product.Id,
            null,
            new DocumentationArticleSaveRequest(
                categoryId.Succeeded ? categoryId.Value : null,
                "first-upload",
                "اولین بارگذاری",
                "Your first upload",
                "در چند دقیقه اولین سند خود را بارگذاری کنید.",
                "Upload your first document in a few minutes.",
                "## پیش از شروع\n\nمطمئن شوید عضویت شما فعال است.",
                "## Before you start\n\nMake sure your membership is active.",
                ContentVisibility.Public,
                null,
                10,
                true,
                null),
            cancellationToken);

        if (articleId.Succeeded)
        {
            await _content.SaveStepsAsync(
                articleId.Value,
                [
                    new StepInput("ورود به پرتال", "Sign in", "با حساب خود وارد شوید.", "Sign in with your account."),
                    new StepInput("انتخاب فایل", "Choose a file", "فایل را بکشید و رها کنید.", "Drag and drop the file."),
                ],
                cancellationToken);
        }

        _logger.LogInformation("Seeded sample product content for 'vault'.");
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
}
