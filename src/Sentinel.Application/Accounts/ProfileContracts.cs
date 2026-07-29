using Sentinel.Application.Common;

namespace Sentinel.Application.Accounts;

public sealed record ProfileEditModel(
    Guid UserId,
    string DisplayName,
    string UserName,
    string? Email,
    string? PhoneNumber,
    string PreferredCulture,
    string TimeZoneId);

public sealed record UpdateProfileRequest(
    string DisplayName,
    string? PhoneNumber,
    string PreferredCulture,
    string TimeZoneId);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// What a member may change about their own account.
/// <para>
/// Deliberately narrow. Roles, status, membership and entitlements are all absent: those are
/// operator decisions, and a self-service endpoint that accepted them would be a privilege
/// escalation waiting to be found. The user id is never taken from the request either — the
/// caller passes the authenticated principal's id and nothing else.
/// </para>
/// </summary>
public interface IProfileService
{
    Task<ProfileEditModel?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<OperationResult> UpdateAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the password after verifying the current one.
    /// <para>
    /// Requiring the current password is what stops a borrowed, unlocked browser from becoming
    /// a permanent account takeover. On success the security stamp rotates, which invalidates
    /// every cookie already issued.
    /// </para>
    /// </summary>
    Task<OperationResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);
}
