using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Identity;
using Sentinel.Application.Security;
using Sentinel.Application.Users;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Security;

namespace Sentinel.Infrastructure.Users;

public sealed class UserAdminService : IUserAdminService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISentinelDbContext _db;
    private readonly IAuditService _audit;
    private readonly IUserSessionService _sessions;
    private readonly IClientContext _clientContext;
    private readonly TimeProvider _timeProvider;

    public UserAdminService(
        UserManager<ApplicationUser> userManager,
        ISentinelDbContext db,
        IAuditService audit,
        IUserSessionService sessions,
        IClientContext clientContext,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _db = db;
        _audit = audit;
        _sessions = sessions;
        _clientContext = clientContext;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult<Guid>> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var phoneWasProvided);

        if (phoneWasProvided && normalizedPhone is null)
        {
            return OperationResult<Guid>.Failure(OperationErrors.InvalidPhone);
        }

        if (normalizedPhone is not null && await PhoneIsTakenAsync(normalizedPhone, null, cancellationToken))
        {
            return OperationResult<Guid>.Failure(OperationErrors.PhoneTaken);
        }

        var roleFailure = ValidateRoles(request.Roles);
        if (roleFailure is not null)
        {
            return OperationResult<Guid>.Failure(roleFailure);
        }

        var user = new ApplicationUser
        {
            Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
            UserName = request.UserName.Trim(),
            Email = request.Email.Trim(),
            EmailConfirmed = true,
            PhoneNumber = phoneWasProvided ? request.PhoneNumber!.Trim() : null,
            NormalizedPhoneNumber = normalizedPhone,
            DisplayName = request.DisplayName.Trim(),
            Status = UserAccountStatus.Active,
            PreferredCulture = request.PreferredCulture,
            TimeZoneId = request.TimeZoneId,
        };

        // Identity hashes the password; the plaintext never reaches the database, a log sink
        // or an audit row.
        var created = await _userManager.CreateAsync(user, request.Password);

        if (!created.Succeeded)
        {
            return OperationResult<Guid>.Failure(TranslateIdentityFailure(created), DescribeCodes(created));
        }

        if (request.Roles.Count > 0)
        {
            var assigned = await _userManager.AddToRolesAsync(user, request.Roles);
            if (!assigned.Succeeded)
            {
                return OperationResult<Guid>.Failure(
                    OperationErrors.IdentityRejected, DescribeCodes(assigned));
            }
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.UserCreated, nameof(ApplicationUser), user.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("userName", user.UserName)
                    .Set("roles", string.Join(",", request.Roles))
                    .Set("hasPhone", normalizedPhone is not null),
            },
            cancellationToken);

        return OperationResult<Guid>.Success(user.Id);
    }

    public async Task<OperationResult> UpdateProfileAsync(
        Guid userId,
        UpdateUserProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var phoneWasProvided);

        if (phoneWasProvided && normalizedPhone is null)
        {
            return OperationResult.Failure(OperationErrors.InvalidPhone);
        }

        if (normalizedPhone is not null
            && await PhoneIsTakenAsync(normalizedPhone, userId, cancellationToken))
        {
            return OperationResult.Failure(OperationErrors.PhoneTaken);
        }

        var metadata = AuditMetadata.Create();

        if (!string.Equals(user.DisplayName, request.DisplayName, StringComparison.Ordinal))
        {
            metadata.SetChange("displayName", user.DisplayName, request.DisplayName);
        }

        if (!string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase))
        {
            metadata.SetChange("email", user.Email, request.Email);

            var emailResult = await _userManager.SetEmailAsync(user, request.Email.Trim());
            if (!emailResult.Succeeded)
            {
                return OperationResult.Failure(TranslateIdentityFailure(emailResult), DescribeCodes(emailResult));
            }

            // Administrators create and correct these addresses, so treat them as confirmed
            // rather than locking the member out of a portal that has no mail transport yet.
            user.EmailConfirmed = true;
        }

        if (!string.Equals(user.NormalizedPhoneNumber, normalizedPhone, StringComparison.Ordinal))
        {
            metadata.SetChange("phone", user.NormalizedPhoneNumber, normalizedPhone);
        }

        user.DisplayName = request.DisplayName.Trim();
        user.PhoneNumber = phoneWasProvided ? request.PhoneNumber!.Trim() : null;
        user.NormalizedPhoneNumber = normalizedPhone;
        user.PreferredCulture = request.PreferredCulture;
        user.TimeZoneId = request.TimeZoneId;

        var updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return OperationResult.Failure(TranslateIdentityFailure(updated), DescribeCodes(updated));
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.UserUpdated, nameof(ApplicationUser), userId) with
            {
                Metadata = metadata,
            },
            cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ChangeStatusAsync(
        Guid userId,
        ChangeUserStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        // An administrator disabling their own account would lock themselves out of the only
        // place the change could be undone.
        if (_clientContext.UserId == userId && request.Status != UserAccountStatus.Active)
        {
            return OperationResult.Failure(OperationErrors.CannotChangeOwnStatus);
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        if (request.Status == UserAccountStatus.Disabled
            && await WouldRemoveLastSuperAdminAsync(user, cancellationToken))
        {
            return OperationResult.Failure(OperationErrors.LastSuperAdmin);
        }

        var previousStatus = user.Status;

        user.Status = request.Status;
        user.SuspendedUntil = request.Status == UserAccountStatus.Suspended ? request.SuspendedUntil : null;
        user.StatusNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        var updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return OperationResult.Failure(OperationErrors.IdentityRejected, DescribeCodes(updated));
        }

        if (request.Status != UserAccountStatus.Active)
        {
            // Blocking sign-in is not enough on its own: an already-issued cookie would keep
            // working until it expired, so every live session is revoked here and now.
            await _sessions.RevokeAllForUserAsync(
                userId, SessionRevocationReason.AdminRevoked, exceptSessionId: null, cancellationToken);

            // Rotating the stamp invalidates any cookie Identity would otherwise still accept.
            await _userManager.UpdateSecurityStampAsync(user);
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.UserStatusChanged, nameof(ApplicationUser), userId) with
            {
                Metadata = AuditMetadata.Create()
                    .SetChange("status", previousStatus, request.Status)
                    .Set("suspendedUntil", request.SuspendedUntil)
                    .Set("note", user.StatusNote),
            },
            cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SetRolesAsync(
        Guid userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
    {
        var roleFailure = ValidateRoles(roles);
        if (roleFailure is not null)
        {
            return OperationResult.Failure(roleFailure);
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        var current = await _userManager.GetRolesAsync(user);

        // Dropping your own SuperAdmin role is a one-way door out of the admin area.
        if (_clientContext.UserId == userId
            && current.Contains(RoleNames.SuperAdmin)
            && !roles.Contains(RoleNames.SuperAdmin))
        {
            return OperationResult.Failure(OperationErrors.CannotRemoveOwnAdminRole);
        }

        if (current.Contains(RoleNames.SuperAdmin)
            && !roles.Contains(RoleNames.SuperAdmin)
            && await IsLastSuperAdminAsync(userId, cancellationToken))
        {
            return OperationResult.Failure(OperationErrors.LastSuperAdmin);
        }

        var toRemove = current.Except(roles, StringComparer.Ordinal).ToList();
        var toAdd = roles.Except(current, StringComparer.Ordinal).ToList();

        if (toRemove.Count > 0)
        {
            var removed = await _userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removed.Succeeded)
            {
                return OperationResult.Failure(OperationErrors.IdentityRejected, DescribeCodes(removed));
            }
        }

        if (toAdd.Count > 0)
        {
            var added = await _userManager.AddToRolesAsync(user, toAdd);
            if (!added.Succeeded)
            {
                return OperationResult.Failure(OperationErrors.IdentityRejected, DescribeCodes(added));
            }
        }

        if (toRemove.Count > 0 || toAdd.Count > 0)
        {
            // Role claims live in the authentication cookie, so an unchanged cookie would keep
            // the old permissions until it expired. Rotating the stamp forces a refresh.
            await _userManager.UpdateSecurityStampAsync(user);
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.UserRolesChanged, nameof(ApplicationUser), userId) with
            {
                Metadata = AuditMetadata.Create()
                    .SetChange("roles", string.Join(",", current.Order()), string.Join(",", roles.Order())),
            },
            cancellationToken);

        return OperationResult.Success();
    }

    private static string? NormalizePhone(string? input, out bool wasProvided)
    {
        wasProvided = !string.IsNullOrWhiteSpace(input);
        return wasProvided ? PhoneNumberNormalizer.Normalize(input) : null;
    }

    private Task<bool> PhoneIsTakenAsync(
        string normalizedPhone,
        Guid? exceptUserId,
        CancellationToken cancellationToken) =>
        _db.Users.AnyAsync(
            u => u.NormalizedPhoneNumber == normalizedPhone
                 && (exceptUserId == null || u.Id != exceptUserId),
            cancellationToken);

    private static string? ValidateRoles(IReadOnlyList<string> roles) =>
        roles.All(role => RoleNames.All.Contains(role, StringComparer.Ordinal))
            ? null
            : OperationErrors.UnknownRole;

    private async Task<bool> WouldRemoveLastSuperAdminAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user);

        return roles.Contains(RoleNames.SuperAdmin)
               && await IsLastSuperAdminAsync(user.Id, cancellationToken);
    }

    /// <summary>
    /// Guards against locking everyone out of the administration area, which no other part of
    /// the application could undo.
    /// </summary>
    private async Task<bool> IsLastSuperAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        var superAdmins = await _userManager.GetUsersInRoleAsync(RoleNames.SuperAdmin);

        return superAdmins.Count(u => u.Status == UserAccountStatus.Active || u.Id == userId) <= 1;
    }

    private static string TranslateIdentityFailure(IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToList();

        if (codes.Any(code => code.Contains("DuplicateUserName", StringComparison.Ordinal)))
        {
            return OperationErrors.UserNameTaken;
        }

        if (codes.Any(code => code.Contains("DuplicateEmail", StringComparison.Ordinal)))
        {
            return OperationErrors.EmailTaken;
        }

        return codes.Any(code => code.StartsWith("Password", StringComparison.Ordinal))
            ? OperationErrors.PasswordRejected
            : OperationErrors.IdentityRejected;
    }

    /// <summary>
    /// Identity's error codes only — never its descriptions, which are English prose and would
    /// bypass the localisation layer on their way to a view.
    /// </summary>
    private static IReadOnlyList<string> DescribeCodes(IdentityResult result) =>
        result.Errors.Select(e => e.Code).ToList();
}
