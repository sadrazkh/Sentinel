using Sentinel.Domain.Products;

namespace Sentinel.Application.Access;

/// <summary>
/// Everything the portal needs to know about one member's relationship to one product.
/// <para>
/// One record rather than scattered booleans computed at each call site. Every controller and
/// every view reads this, so the button a member sees and the endpoint that would run cannot
/// disagree — a card offering "Open" for something the launch endpoint refuses is the failure
/// this exists to prevent.
/// </para>
/// </summary>
public sealed record ProductAccessDecision(
    ProductAccessStatus Status,
    ProductPrimaryAction PrimaryAction,
    bool CanView,
    bool CanLaunch,
    bool CanDownload,
    bool CanPurchase,
    bool CanRenew,
    bool CanViewPrivateDocs,
    bool CanManageService,
    /// <summary>Why access was refused, for the lock message. <see cref="AccessDenialReason.None"/> when allowed.</summary>
    AccessDenialReason DenialReason)
{
    /// <summary>The product is not visible to this member at all — it must not appear in a list.</summary>
    public static readonly ProductAccessDecision Hidden = new(
        ProductAccessStatus.Locked,
        ProductPrimaryAction.None,
        CanView: false,
        CanLaunch: false,
        CanDownload: false,
        CanPurchase: false,
        CanRenew: false,
        CanViewPrivateDocs: false,
        CanManageService: false,
        AccessDenialReason.ApplicationNotPublished);

    public bool IsUsable => CanLaunch || CanDownload || CanManageService;

    /// <summary>Listed but not usable — the member can see what obtaining it would give them.</summary>
    public bool IsVisibleButLocked => CanView && !IsUsable;
}
