using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Products;
using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.IntegrationTests.Infrastructure;

/// <summary>
/// Builds members, applications and grants through the real services, so the rows under test
/// are shaped exactly as the running application would shape them.
/// </summary>
public static class PortalTestData
{
    /// <summary>Synthetic and used only by this suite. Never a real credential.</summary>
    public const string MemberPassword = "Portal-Member-Test-246810";

    public static Task<Guid> CreateMemberAsync(
        this SentinelWebApplicationFactory factory,
        string userName,
        MembershipTier tier = MembershipTier.Pro,
        MembershipAdminState membershipState = MembershipAdminState.Active,
        DateTimeOffset? membershipEndsAt = null,
        DateTimeOffset? membershipStartsAt = null,
        bool withMembership = true,
        UserAccountStatus accountStatus = UserAccountStatus.Active,
        DateTimeOffset? suspendedUntil = null) =>
        factory.WithScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var db = services.GetRequiredService<ISentinelDbContext>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

            // Idempotent, so a [Theory] whose cases share a subject does not fail on the
            // second case with a duplicate-username error.
            if (await userManager.FindByNameAsync(userName) is { } existing)
            {
                return existing.Id;
            }

            var user = new ApplicationUser
            {
                Id = SequentialGuid.New(),
                UserName = userName,
                Email = $"{userName}@sentinel.invalid",
                EmailConfirmed = true,
                DisplayName = userName,
                Status = accountStatus,
                SuspendedUntil = suspendedUntil,
            };

            var created = await userManager.CreateAsync(user, MemberPassword);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            await userManager.AddToRoleAsync(user, RoleNames.Member);

            if (withMembership)
            {
                db.Memberships.Add(new Membership
                {
                    Id = SequentialGuid.New(),
                    UserId = user.Id,
                    Tier = tier,
                    AdminState = membershipState,
                    StartsAt = membershipStartsAt ?? now.AddDays(-30),
                    EndsAt = membershipEndsAt ?? now.AddDays(30),
                });

                await db.SaveChangesAsync();
            }

            return user.Id;
        });

    public static Task<Guid> CreateApplicationAsync(
        this SentinelWebApplicationFactory factory,
        string key,
        bool requiresExplicitEntitlement = false,
        MembershipTier? minimumTier = null,
        ProductReleaseStatus releaseStatus = ProductReleaseStatus.Stable,
        bool isEnabled = true,
        string launchUrl = "https://apps.example.com/target") =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            var application = new Product
            {
                Id = SequentialGuid.New(),
                Key = key,
                NameFa = $"برنامهٔ {key}",
                NameEn = $"Application {key}",
                DescriptionFa = "توضیح آزمایشی.",
                DescriptionEn = "Test description.",
                LaunchUrl = launchUrl,
                ReleaseStatus = releaseStatus,
                IsEnabled = isEnabled,
                DisplayOrder = 10,
                RequiresExplicitEntitlement = requiresExplicitEntitlement,
                MinimumTier = minimumTier,
            };

            db.Products.Add(application);
            await db.SaveChangesAsync();

            return application.Id;
        });

    /// <summary>
    /// A product with the library-specific fields set. Separate from
    /// <see cref="CreateApplicationAsync"/> so the launch-endpoint suite keeps its short,
    /// stable signature while the library suite can shape capabilities and categories.
    /// </summary>
    public static Task<Guid> CreateProductAsync(
        this SentinelWebApplicationFactory factory,
        string key,
        ProductCapability capabilities = ProductCapability.Launchable,
        ProductType type = ProductType.WebApplication,
        ProductReleaseStatus releaseStatus = ProductReleaseStatus.Stable,
        bool isEnabled = true,
        bool requiresExplicitEntitlement = false,
        MembershipTier? minimumTier = null,
        Guid? categoryId = null,
        string? summaryEn = null,
        string? launchUrl = "https://apps.example.com/target") =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            var product = new Product
            {
                Id = SequentialGuid.New(),
                Key = key,
                NameFa = $"محصول {key}",
                NameEn = $"Product {key}",
                SummaryEn = summaryEn,
                DescriptionEn = "Test description.",
                LaunchUrl = launchUrl,
                Type = type,
                Capabilities = capabilities,
                CategoryId = categoryId,
                ReleaseStatus = releaseStatus,
                IsEnabled = isEnabled,
                DisplayOrder = 10,
                RequiresExplicitEntitlement = requiresExplicitEntitlement,
                MinimumTier = minimumTier,
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();

            return product.Id;
        });

    public static Task<Guid> CreateProductCategoryAsync(
        this SentinelWebApplicationFactory factory,
        string key,
        bool isVisible = true) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            if (await db.ProductCategories.FirstOrDefaultAsync(c => c.Key == key) is { } existing)
            {
                return existing.Id;
            }

            var category = new ProductCategory
            {
                Id = SequentialGuid.New(),
                Key = key,
                NameFa = $"دستهٔ {key}",
                NameEn = $"Category {key}",
                IsVisible = isVisible,
            };

            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();

            return category.Id;
        });

    public static Task GrantAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        Guid productId,
        bool isEnabled = true,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null,
        EntitlementSource source = EntitlementSource.AdminGrant) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

            db.ProductEntitlements.Add(new ProductEntitlement
            {
                Id = SequentialGuid.New(),
                UserId = userId,
                ProductId = productId,
                Source = source,
                IsEnabled = isEnabled,
                StartsAt = startsAt ?? now.AddDays(-1),
                ExpiresAt = expiresAt,
                RevokedAt = revokedAt,
            });

            await db.SaveChangesAsync();
        });

    /// <summary>Changes an account's status the way an administrator later will.</summary>
    public static Task SetAccountStatusAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        UserAccountStatus status,
        DateTimeOffset? suspendedUntil = null) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(u => u.Status, status)
                    .SetProperty(u => u.SuspendedUntil, suspendedUntil));
        });

    public static Task AddToRoleAsync(
        this SentinelWebApplicationFactory factory,
        string userName,
        string role) =>
        factory.WithScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByNameAsync(userName);

            Assert.NotNull(user);
            var result = await userManager.AddToRoleAsync(user!, role);
            Assert.True(result.Succeeded);
        });

    public static Task<UserAccountStatus> GetAccountStatusAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.Status)
                .FirstAsync();
        });

    public static Task<IList<string>> GetRolesAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());

            Assert.NotNull(user);
            return await userManager.GetRolesAsync(user!);
        });

    public static Task<bool> UserExistsAsync(
        this SentinelWebApplicationFactory factory,
        string userName) =>
        factory.WithScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            return await userManager.FindByNameAsync(userName) is not null;
        });

    public static Task<Guid> GetUserIdAsync(
        this SentinelWebApplicationFactory factory,
        string userName) =>
        factory.WithScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByNameAsync(userName);

            Assert.NotNull(user);
            return user!.Id;
        });

    public static Task<Membership?> GetMembershipAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.Memberships
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId);
        });

    public static Task<List<string>> RecentAuditActionsAsync(
        this SentinelWebApplicationFactory factory,
        string entityId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.AuditLogs
                .AsNoTracking()
                .Where(a => a.EntityId == entityId)
                .OrderBy(a => a.OccurredAt)
                .Select(a => a.Action)
                .ToListAsync();
        });
}
