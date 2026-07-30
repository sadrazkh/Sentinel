using Sentinel.Application.Access;
using Sentinel.Application.Content;
using Sentinel.Domain.Products;

namespace Sentinel.UnitTests.Content;

public sealed class ContentAudienceTests
{
    private static ContentAudience Viewer(
        bool canSeeProduct = true,
        bool isEntitled = false,
        bool isOperator = false) =>
        new(canSeeProduct, isEntitled, isOperator);

    [Fact]
    public void Somebody_who_cannot_see_the_product_reads_nothing()
    {
        var viewer = Viewer(canSeeProduct: false, isEntitled: true);

        Assert.False(viewer.Allows(ContentVisibility.Public));
        Assert.False(viewer.Allows(ContentVisibility.Entitled));
        Assert.False(viewer.Allows(ContentVisibility.Internal));
    }

    [Fact]
    public void Public_content_serves_the_pre_purchase_audience()
    {
        // Somebody deciding whether to obtain the product must be able to read about it.
        Assert.True(Viewer(isEntitled: false).Allows(ContentVisibility.Public));
    }

    [Fact]
    public void Entitled_content_needs_usable_access()
    {
        Assert.False(Viewer(isEntitled: false).Allows(ContentVisibility.Entitled));
        Assert.True(Viewer(isEntitled: true).Allows(ContentVisibility.Entitled));
    }

    [Fact]
    public void Internal_content_is_for_operators_only()
    {
        Assert.False(Viewer(isEntitled: true).Allows(ContentVisibility.Internal));
        Assert.True(Viewer(isOperator: true).Allows(ContentVisibility.Internal));
    }

    [Fact]
    public void An_operator_reads_every_audience()
    {
        var operatorViewer = Viewer(canSeeProduct: false, isEntitled: false, isOperator: true);

        Assert.True(operatorViewer.Allows(ContentVisibility.Public));
        Assert.True(operatorViewer.Allows(ContentVisibility.Entitled));
        Assert.True(operatorViewer.Allows(ContentVisibility.Internal));
    }

    [Fact]
    public void An_unrecognised_visibility_hides_rather_than_leaks()
    {
        // A new enum member added without updating the switch must default to hidden.
        Assert.False(Viewer(isOperator: true).Allows((ContentVisibility)999));
    }

    // ------------------------------------------------------------------------ derivation ----

    [Fact]
    public void A_lapsed_member_reads_the_public_pages_but_not_the_entitled_ones()
    {
        // Taken from usability, not from holding a grant row: an expired arrangement should read
        // like no arrangement for the purposes of setup instructions.
        var lapsed = new ProductAccessDecision(
            ProductAccessStatus.Expired,
            ProductPrimaryAction.ViewDetails,
            CanView: true,
            CanLaunch: false,
            CanDownload: false,
            CanPurchase: false,
            CanRenew: true,
            CanViewPrivateDocs: false,
            CanManageService: false,
            AccessDenialReason.MembershipInvalid);

        var audience = ContentAudience.From(lapsed);

        Assert.True(audience.Allows(ContentVisibility.Public));
        Assert.False(audience.Allows(ContentVisibility.Entitled));
    }

    [Fact]
    public void A_member_with_usable_access_reads_the_entitled_pages()
    {
        var active = new ProductAccessDecision(
            ProductAccessStatus.Active,
            ProductPrimaryAction.Open,
            CanView: true,
            CanLaunch: true,
            CanDownload: false,
            CanPurchase: false,
            CanRenew: false,
            CanViewPrivateDocs: true,
            CanManageService: false,
            AccessDenialReason.None);

        Assert.True(ContentAudience.From(active).Allows(ContentVisibility.Entitled));
    }

    [Fact]
    public void A_hidden_product_yields_an_audience_that_reads_nothing()
    {
        var audience = ContentAudience.From(ProductAccessDecision.Hidden);

        Assert.False(audience.Allows(ContentVisibility.Public));
    }
}
