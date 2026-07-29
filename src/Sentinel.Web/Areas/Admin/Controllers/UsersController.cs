using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Entitlements;
using Sentinel.Application.Users;
using Sentinel.Domain.Identity;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// User administration.
/// <para>
/// Read is open to Support; every mutation additionally requires
/// <see cref="PolicyNames.BackOfficeWrite"/>, and role assignment requires SuperAdmin. The
/// policies are declared per action rather than once on the controller, because a single
/// controller-wide attribute would quietly grant Support the ability to write.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
public sealed class UsersController : Controller
{
    private readonly IUserAdminQuery _query;
    private readonly IUserAdminService _users;
    private readonly IMembershipAdminService _memberships;
    private readonly IEntitlementAdminQuery _entitlementQuery;
    private readonly IEntitlementAdminService _entitlements;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly TimeProvider _timeProvider;

    public UsersController(
        IUserAdminQuery query,
        IUserAdminService users,
        IMembershipAdminService memberships,
        IEntitlementAdminQuery entitlementQuery,
        IEntitlementAdminService entitlements,
        IStringLocalizer<SharedResource> localizer,
        TimeProvider timeProvider)
    {
        _query = query;
        _users = users;
        _memberships = memberships;
        _entitlementQuery = entitlementQuery;
        _entitlements = entitlements;
        _localizer = localizer;
        _timeProvider = timeProvider;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    private bool CanManageRoles => User.IsInRole(RoleNames.SuperAdmin);

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private string AdminTimeZone => UserTime.DefaultTimeZoneId;

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] UserListFilterViewModel filter,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            // A tampered query string falls back to the defaults rather than erroring out.
            filter = new UserListFilterViewModel();
            ModelState.Clear();
        }

        var results = await _query.SearchAsync(
            filter.ToRequest(_timeProvider.GetUtcNow()), cancellationToken);

        // Normalised by the query service; reflect that back so the pager matches reality.
        filter.Page = results.Page;
        filter.PageSize = results.PageSize;

        return View(new UserListViewModel
        {
            Results = results,
            Filter = filter,
            TimeZoneId = AdminTimeZone,
            CanWrite = CanWrite,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var detail = await _query.GetDetailAsync(id, cancellationToken);

        if (detail is null)
        {
            return NotFound();
        }

        return View(BuildDetailViewModel(detail));
    }

    [HttpGet]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult Create() => View(new CreateUserViewModel());

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(CreateUserViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _users.CreateAsync(
            new CreateUserRequest(
                model.UserName,
                model.DisplayName,
                model.Email,
                model.PhoneNumber,
                model.Password,
                model.Roles,
                model.PreferredCulture,
                model.TimeZoneId),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View(model);
        }

        TempData["StatusMessage"] = _localizer["admin.user.created"].Value;
        return RedirectToAction(nameof(Details), new { id = result.Value });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> UpdateProfile(
        EditUserProfileViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await RedisplayDetailsAsync(model.UserId, cancellationToken);
        }

        var result = await _users.UpdateProfileAsync(
            model.UserId,
            new UpdateUserProfileRequest(
                model.DisplayName,
                model.Email,
                model.PhoneNumber,
                model.PreferredCulture,
                model.TimeZoneId),
            cancellationToken);

        return await CompleteAsync(result, model.UserId, "admin.user.profileSaved", cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> ChangeStatus(
        ChangeUserStatusViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await RedisplayDetailsAsync(model.UserId, cancellationToken);
        }

        var result = await _users.ChangeStatusAsync(
            model.UserId,
            new ChangeUserStatusRequest(
                model.Status,
                // A suspension deadline is inclusive of the day the operator picked.
                model.SuspendedUntil is { } until ? EndOfDayUtc(until) : null,
                model.Note),
            cancellationToken);

        return await CompleteAsync(result, model.UserId, "admin.user.statusSaved", cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.SystemAdministration)]
    public async Task<IActionResult> SetRoles(
        SetUserRolesViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await RedisplayDetailsAsync(model.UserId, cancellationToken);
        }

        var result = await _users.SetRolesAsync(model.UserId, model.Roles, cancellationToken);

        return await CompleteAsync(result, model.UserId, "admin.user.rolesSaved", cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> SaveMembership(
        MembershipEditViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await RedisplayDetailsAsync(model.UserId, cancellationToken);
        }

        var result = await _memberships.SaveAsync(
            model.UserId, ToRequest(model), cancellationToken);

        return await CompleteAsync(result, model.UserId, "admin.membership.saved", cancellationToken);
    }

    /// <summary>
    /// The entitlement editor: every application alongside this user's grant state and the
    /// decision the access rules currently reach for it.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Entitlements(Guid id, CancellationToken cancellationToken)
    {
        var detail = await _query.GetDetailAsync(id, cancellationToken);

        if (detail is null)
        {
            return NotFound();
        }

        var rows = await _entitlementQuery.GetForUserAsync(id, cancellationToken);

        return View(new UserEntitlementsViewModel
        {
            UserId = id,
            UserDisplayName = detail.DisplayName,
            Rows = rows,
            CanWrite = CanWrite,
            TimeZoneId = AdminTimeZone,
        });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> GrantEntitlement(
        GrantEntitlementViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return RedirectToEntitlements(model.UserId, "admin.error.identityRejected");
        }

        var result = await _entitlements.GrantAsync(
            model.UserId,
            model.ApplicationId,
            new GrantEntitlementRequest(
                model.StartsAt is { } startsAt ? StartOfDayUtc(startsAt) : null,
                model.ExpiresAt is { } expiresAt ? EndOfDayUtc(expiresAt) : null,
                model.Notes,
                model.ConcurrencyToken),
            cancellationToken);

        return RedirectToEntitlements(
            model.UserId, result.Succeeded ? "admin.entitlement.granted" : result.ErrorKey);
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> RevokeEntitlement(
        GrantEntitlementViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await _entitlements.RevokeAsync(
            model.UserId, model.ApplicationId, model.Notes, model.ConcurrencyToken, cancellationToken);

        return RedirectToEntitlements(
            model.UserId, result.Succeeded ? "admin.entitlement.revokedMessage" : result.ErrorKey);
    }

    private IActionResult RedirectToEntitlements(Guid userId, string? messageKey)
    {
        if (messageKey is not null)
        {
            TempData["StatusMessage"] = _localizer[messageKey].Value;
        }

        return RedirectToAction(nameof(Entitlements), new { id = userId });
    }

    /// <summary>
    /// Live preview for the membership editor. It runs the real
    /// <c>IMembershipStatusResolver</c> on the server rather than reimplementing the rules in
    /// JavaScript, so what the editor promises and what the portal enforces cannot diverge.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult PreviewMembership([FromForm] MembershipEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var snapshot = _memberships.Preview(ToRequest(model));

        return Json(new
        {
            status = _localizer[AccessPresentation.MembershipStatusKey(snapshot.Status)].Value,
            badgeClass = AccessPresentation.MembershipBadgeClass(snapshot.Status),
            grantsAccess = snapshot.GrantsAccess,
            daysRemaining = snapshot.DaysRemaining,
            accessEndsAt = snapshot.AccessEndsAt is { } accessEndsAt
                ? UserTime.Format(accessEndsAt, AdminTimeZone, "yyyy/MM/dd")
                : null,
        });
    }

    private MembershipEditRequest ToRequest(MembershipEditViewModel model) => new(
        model.Tier,
        model.AdminState,
        // Dates are entered as calendar days and stored as UTC instants: a start date opens at
        // midnight and an end date runs through the end of that day, which is what "ends on the
        // 30th" means to the person typing it.
        StartOfDayUtc(model.StartsAt),
        model.EndsAt is { } endsAt ? EndOfDayUtc(endsAt) : null,
        model.GracePeriodDaysOverride,
        model.Notes,
        model.ConcurrencyToken);

    private static DateTimeOffset StartOfDayUtc(DateTime date) =>
        new(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc));

    private static DateTimeOffset EndOfDayUtc(DateTime date) =>
        new(DateTime.SpecifyKind(date.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc));

    private async Task<IActionResult> CompleteAsync(
        OperationResult result,
        Guid userId,
        string successKey,
        CancellationToken cancellationToken)
    {
        if (result.Succeeded)
        {
            TempData["StatusMessage"] = _localizer[successKey].Value;
            return RedirectToAction(nameof(Details), new { id = userId });
        }

        AddError(result);
        return await RedisplayDetailsAsync(userId, cancellationToken);
    }

    private async Task<IActionResult> RedisplayDetailsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var detail = await _query.GetDetailAsync(userId, cancellationToken);

        if (detail is null)
        {
            return NotFound();
        }

        return View(nameof(Details), BuildDetailViewModel(detail));
    }

    private UserDetailViewModel BuildDetailViewModel(UserDetail detail) => new()
    {
        User = detail,
        TimeZoneId = AdminTimeZone,
        CanWrite = CanWrite,
        CanManageRoles = CanManageRoles,
        IsSelf = CurrentUserId == detail.Id,
    };

    /// <summary>
    /// Surfaces a service failure as a localised model error. The service returns a key, never
    /// a message, so nothing here can leak an internal exception string into a page.
    /// </summary>
    private void AddError(OperationResult result)
    {
        var key = result.ErrorKey ?? OperationErrors.IdentityRejected;
        ModelState.AddModelError(string.Empty, _localizer[key].Value);
    }
}
