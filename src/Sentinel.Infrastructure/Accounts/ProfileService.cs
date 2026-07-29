using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Accounts;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Identity;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Identity;

namespace Sentinel.Infrastructure.Accounts;

public sealed class ProfileService : IProfileService
{
    private static readonly string[] AllowedCultures = ["fa", "en"];

    private readonly ISentinelDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _audit;

    public ProfileService(
        ISentinelDbContext db,
        UserManager<ApplicationUser> userManager,
        IAuditService audit)
    {
        _db = db;
        _userManager = userManager;
        _audit = audit;
    }

    public Task<ProfileEditModel?> GetAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new ProfileEditModel(
                u.Id,
                u.DisplayName,
                u.UserName!,
                u.Email,
                u.PhoneNumber,
                u.PreferredCulture,
                u.TimeZoneId))
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<OperationResult> UpdateAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var displayName = request.DisplayName.Trim();

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return OperationResult.Failure(OperationErrors.IdentityRejected);
        }

        // Both are allow-listed rather than trusted: the culture reaches the localisation
        // middleware and the time zone reaches TimeZoneInfo lookup.
        var culture = AllowedCultures.Contains(request.PreferredCulture, StringComparer.OrdinalIgnoreCase)
            ? request.PreferredCulture.ToLowerInvariant()
            : "fa";

        if (!IsKnownTimeZone(request.TimeZoneId))
        {
            return OperationResult.Failure(ProfileErrors.UnknownTimeZone);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        string? normalizedPhone = null;

        if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            normalizedPhone = PhoneNumberNormalizer.Normalize(request.PhoneNumber);

            if (normalizedPhone is null)
            {
                return OperationResult.Failure(OperationErrors.InvalidPhone);
            }

            // Checked before saving so the operator sees a clear message rather than a unique
            // index violation surfacing as a 500.
            var taken = await _db.Users.AnyAsync(
                u => u.Id != userId && u.NormalizedPhoneNumber == normalizedPhone, cancellationToken);

            if (taken)
            {
                return OperationResult.Failure(OperationErrors.PhoneTaken);
            }
        }

        var metadata = AuditMetadata.Create();

        if (!string.Equals(user.DisplayName, displayName, StringComparison.Ordinal))
        {
            metadata.SetChange("displayName", user.DisplayName, displayName);
        }

        if (!string.Equals(user.NormalizedPhoneNumber, normalizedPhone, StringComparison.Ordinal))
        {
            metadata.SetChange("phone", user.NormalizedPhoneNumber, normalizedPhone);
        }

        if (!string.Equals(user.TimeZoneId, request.TimeZoneId, StringComparison.Ordinal))
        {
            metadata.SetChange("timeZone", user.TimeZoneId, request.TimeZoneId);
        }

        user.DisplayName = displayName;
        user.PhoneNumber = normalizedPhone;
        user.NormalizedPhoneNumber = normalizedPhone;
        user.PreferredCulture = culture;
        user.TimeZoneId = request.TimeZoneId;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.UserUpdated, nameof(ApplicationUser), userId) with
            {
                Metadata = metadata.IsEmpty ? null : metadata,
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        // ChangePasswordAsync verifies the current password itself and rotates the security
        // stamp on success, which is what invalidates cookies issued before the change.
        var result = await _userManager.ChangePasswordAsync(
            user, request.CurrentPassword, request.NewPassword);

        if (!result.Succeeded)
        {
            // One message for a wrong current password and for a policy rejection would be
            // unhelpful, but the codes are kept for the operator rather than shown raw.
            var isCurrentPasswordWrong = result.Errors.Any(e => e.Code == "PasswordMismatch");

            await _audit.RecordAndSaveAsync(
                AuditEntry.For(AuditActions.PasswordChanged, nameof(ApplicationUser), userId) with
                {
                    Result = AuditResult.Failure,
                    Metadata = AuditMetadata.Create()
                        .Set("reason", isCurrentPasswordWrong ? "currentPasswordMismatch" : "policyRejected"),
                },
                cancellationToken);

            return OperationResult.Failure(
                isCurrentPasswordWrong ? ProfileErrors.CurrentPasswordWrong : OperationErrors.PasswordRejected,
                result.Errors.Select(e => e.Description).ToList());
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.PasswordChanged, nameof(ApplicationUser), userId),
            cancellationToken);

        return OperationResult.Success();
    }

    private static bool IsKnownTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public static class ProfileErrors
{
    public const string CurrentPasswordWrong = "profile.error.currentPasswordWrong";
    public const string UnknownTimeZone = "profile.error.unknownTimeZone";
}
