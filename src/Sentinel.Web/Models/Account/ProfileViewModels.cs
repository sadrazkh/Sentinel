using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Accounts;
using Sentinel.Application.Notifications;
using Sentinel.Application.Security;
using Sentinel.Domain.Identity;

namespace Sentinel.Web.Models.Account;

public sealed class ProfileViewModel
{
    [Required(ErrorMessage = "validation.required")]
    [StringLength(ApplicationUser.DisplayNameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "profile.displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(32, ErrorMessage = "validation.tooLong")]
    [Display(Name = "profile.phone")]
    public string? PhoneNumber { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [Display(Name = "profile.language")]
    public string PreferredCulture { get; set; } = "fa";

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ApplicationUser.TimeZoneMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "profile.timeZone")]
    public string TimeZoneId { get; set; } = "Asia/Tehran";

    /// <summary>Read-only context. Neither is editable by the member.</summary>
    public string UserName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public IReadOnlyList<string> TimeZoneOptions { get; set; } = [];

    /// <summary>Rendered read-only; changed through the dedicated Telegram endpoints.</summary>
    public TelegramLinkState? Telegram { get; set; }

    /// <summary>
    /// The freshly issued deep link, if the member just asked to connect. Carried in TempData
    /// and shown once — it embeds a single-use token.
    /// </summary>
    public string? TelegramDeepLink { get; set; }

    public DateTimeOffset? TelegramDeepLinkExpiresAt { get; set; }

    public static ProfileViewModel From(ProfileEditModel model) => new()
    {
        DisplayName = model.DisplayName,
        PhoneNumber = model.PhoneNumber,
        PreferredCulture = model.PreferredCulture,
        TimeZoneId = model.TimeZoneId,
        UserName = model.UserName,
        Email = model.Email,
    };

    public UpdateProfileRequest ToRequest() =>
        new(DisplayName, PhoneNumber, PreferredCulture, TimeZoneId);
}

public sealed class ChangePasswordViewModel
{
    [Required(ErrorMessage = "validation.required")]
    [DataType(DataType.Password)]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [Display(Name = "security.currentPassword")]
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>
    /// Only a ceiling here. The real policy lives in Identity's configured options, so the
    /// form cannot drift out of step with what the server actually enforces.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [DataType(DataType.Password)]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [Display(Name = "security.newPassword")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "security.error.passwordsDoNotMatch")]
    [Display(Name = "security.confirmPassword")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class SecurityViewModel
{
    public required IReadOnlyList<ActiveSessionView> Sessions { get; init; }

    public required string TimeZoneId { get; init; }

    public required int PasswordMinimumLength { get; init; }

    public required bool PasswordRequiresDigit { get; init; }

    public required bool PasswordRequiresUppercase { get; init; }

    public required bool PasswordRequiresNonAlphanumeric { get; init; }

    public ChangePasswordViewModel ChangePassword { get; set; } = new();
}
