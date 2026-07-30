using System.ComponentModel.DataAnnotations;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Provisioning;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class CustomerServiceListViewModel
{
    public required IReadOnlyList<CustomerServiceAdminRow> Services { get; init; }

    public required bool CanWrite { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>
    /// Only the rows an operator has to do something about.
    /// <para>
    /// Surfaced as its own count rather than left for someone to spot in the table: a service parked
    /// after an unknown panel outcome is a customer in limbo, and it is exactly the state that hides
    /// well in a long list of working ones.
    /// </para>
    /// </summary>
    public IReadOnlyList<CustomerServiceAdminRow> NeedingAttention =>
        Services.Where(service => service.NeedsAttention).ToList();
}

/// <summary>
/// What an operator supplies to create a service.
/// <para>
/// A member and a plan, and nothing else. Every term the service will have — traffic, duration,
/// devices, price — is read from the plan row, and the server is chosen by the selector. There is
/// deliberately no field here for a quota, an expiry, a server or an inbound: a bindable property
/// for any of those is how an operator's typo, or a crafted form post, becomes a customer's terms.
/// </para>
/// </summary>
public sealed class CustomerServiceCreateViewModel
{
    [Required(ErrorMessage = "validation.required")]
    [Display(Name = "admin.service.member")]
    public Guid UserId { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [Display(Name = "admin.service.plan")]
    public Guid PlanId { get; set; }

    [StringLength(CustomerService.NotesMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.service.notes")]
    public string? Notes { get; set; }

    /// <summary>Populated by the controller for the pickers; never bound from the request.</summary>
    public IReadOnlyList<(Guid Id, string Label)> Members { get; set; } = [];

    public IReadOnlyList<(Guid Id, string Label)> Plans { get; set; } = [];

    /// <summary>How many accounts the member picker holds, so the hint can say so honestly.</summary>
    public int PickerSize { get; set; }
}

/// <summary>The one number a renewal takes. Bounded here as well as in the manager.</summary>
public sealed class CustomerServiceRenewViewModel
{
    public Guid ServiceId { get; set; }

    [Range(1, 3650, ErrorMessage = "admin.error.planDurationInvalid")]
    [Display(Name = "admin.service.renewDays")]
    public int AdditionalDays { get; set; } = 30;
}
