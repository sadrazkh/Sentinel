using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Catalog;
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
        ApplicationPublishStatus publishStatus = ApplicationPublishStatus.Published,
        bool isEnabled = true,
        bool isBeta = false,
        string launchUrl = "https://apps.example.com/target") =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            var application = new PortalApplication
            {
                Id = SequentialGuid.New(),
                Key = key,
                NameFa = $"برنامهٔ {key}",
                NameEn = $"Application {key}",
                DescriptionFa = "توضیح آزمایشی.",
                DescriptionEn = "Test description.",
                LaunchUrl = launchUrl,
                PublishStatus = publishStatus,
                IsEnabled = isEnabled,
                IsBeta = isBeta,
                DisplayOrder = 10,
                RequiresExplicitEntitlement = requiresExplicitEntitlement,
                MinimumTier = minimumTier,
            };

            db.PortalApplications.Add(application);
            await db.SaveChangesAsync();

            return application.Id;
        });

    public static Task GrantAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        Guid applicationId,
        bool isEnabled = true,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

            db.UserEntitlements.Add(new UserEntitlement
            {
                Id = SequentialGuid.New(),
                UserId = userId,
                ApplicationId = applicationId,
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
