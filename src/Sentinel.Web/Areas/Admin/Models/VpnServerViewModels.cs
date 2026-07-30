using System.ComponentModel.DataAnnotations;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Servers;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class VpnServerListViewModel
{
    public required IReadOnlyList<VpnServerListItem> Servers { get; init; }

    public required bool CanWrite { get; init; }

    public required string TimeZoneId { get; init; }
}

public sealed class VpnServerEditViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(VpnServer.KeyMaxLength, MinimumLength = 2, ErrorMessage = "validation.length")]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "admin.validation.applicationKey")]
    [Display(Name = "admin.vpn.key")]
    public string Key { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(VpnServer.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string NameFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(VpnServer.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string NameEn { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(2, MinimumLength = 2, ErrorMessage = "validation.length")]
    [RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "admin.error.vpnServerCountryInvalid")]
    [Display(Name = "admin.vpn.country")]
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>
    /// Length-checked here only. The scheme, host and query rules live in
    /// <see cref="Sentinel.Vpn.Panel.PanelBaseUrlPolicy"/>, which the save path uses — restating
    /// them in an attribute would create a second copy that drifts.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [StringLength(VpnServer.BaseUrlMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.vpn.baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Left blank on an edit to keep the stored credential.
    /// <para>
    /// Never populated from the database — the portal cannot show a token again once it is saved,
    /// only the hint. That is deliberate: a form that round-trips a credential puts it in the
    /// page source, the browser's autofill and any proxy log along the way.
    /// </para>
    /// </summary>
    [StringLength(512, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.vpn.apiToken")]
    public string? ApiToken { get; set; }

    /// <summary>Read-only, for showing which credential is in place.</summary>
    public string? ApiTokenHint { get; set; }

    [Display(Name = "admin.vpn.status")]
    public VpnServerStatus Status { get; set; } = VpnServerStatus.Unverified;

    [Range(0, 100_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.vpn.maxClients")]
    public int MaxClients { get; set; } = 200;

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.vpn.priority")]
    public int SelectionPriority { get; set; } = 100;

    [StringLength(VpnServer.NotesMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.membership.notes")]
    public string? Notes { get; set; }

    public Guid? ConcurrencyToken { get; set; }

    public bool IsNew => Id == Guid.Empty;

    public static VpnServerEditViewModel From(VpnServerEditModel model) => new()
    {
        Id = model.Id,
        Key = model.Key,
        NameFa = model.NameFa,
        NameEn = model.NameEn,
        CountryCode = model.CountryCode,
        BaseUrl = model.BaseUrl,
        ApiTokenHint = model.ApiTokenHint,
        Status = model.Status,
        MaxClients = model.MaxClients,
        SelectionPriority = model.SelectionPriority,
        Notes = model.Notes,
        ConcurrencyToken = model.ConcurrencyToken,

        // ApiToken is deliberately not carried across.
    };

    public VpnServerSaveRequest ToRequest() => new(
        Key,
        NameFa,
        NameEn,
        CountryCode,
        BaseUrl,
        string.IsNullOrWhiteSpace(ApiToken) ? null : ApiToken,
        Status,
        MaxClients,
        SelectionPriority,
        Notes,
        ConcurrencyToken);
}

public sealed class VpnServerInboundsViewModel
{
    public required Guid ServerId { get; init; }

    public required string ServerNameFa { get; init; }

    public required string ServerNameEn { get; init; }

    public required IReadOnlyList<ServerInboundProfile> Allowlisted { get; init; }

    /// <summary>
    /// <c>null</c> when the panel has not been asked yet — which is different from "the panel has
    /// no inbounds", and the view says so rather than showing an empty list either way.
    /// </summary>
    public IReadOnlyList<DiscoveredInbound>? Discovered { get; init; }

    public string? DiscoveryError { get; init; }

    public required bool CanWrite { get; init; }
}
