using System.ComponentModel.DataAnnotations;

namespace Sentinel.Web.Models.Account;

/// <summary>
/// Bound directly from the login form. A dedicated view model — never an EF entity — so no
/// request can reach a domain property that was not deliberately exposed here.
/// </summary>
public sealed class LoginViewModel
{
    [Required(ErrorMessage = "validation.required")]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [Display(Name = "login.identifier")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// Only a length ceiling is enforced here. Applying the password policy to the *login*
    /// form would tell an attacker what the policy is and would lock out anyone whose
    /// password predates a policy change — the check belongs on the change/create path.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [DataType(DataType.Password)]
    [Display(Name = "login.password")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "login.rememberMe")]
    public bool RememberMe { get; set; }

    /// <summary>Validated with <c>Url.IsLocalUrl</c> before use; see AccountController.</summary>
    public string? ReturnUrl { get; set; }
}
