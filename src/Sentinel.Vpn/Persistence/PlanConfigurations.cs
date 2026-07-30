using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Persistence;

public sealed class ServicePlanConfiguration : IEntityTypeConfiguration<ServicePlan>
{
    public void Configure(EntityTypeBuilder<ServicePlan> builder)
    {
        builder.ToTable("ServicePlans");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Key).HasMaxLength(ServicePlan.KeyMaxLength).IsRequired();
        builder.Property(p => p.NameFa).HasMaxLength(ServicePlan.NameMaxLength).IsRequired();
        builder.Property(p => p.NameEn).HasMaxLength(ServicePlan.NameMaxLength).IsRequired();
        builder.Property(p => p.DescriptionFa).HasMaxLength(ServicePlan.DescriptionMaxLength);
        builder.Property(p => p.DescriptionEn).HasMaxLength(ServicePlan.DescriptionMaxLength);
        builder.Property(p => p.Currency).HasMaxLength(ServicePlan.CurrencyMaxLength).IsRequired();
        builder.Property(p => p.CountryCode).HasMaxLength(2);

        builder.Property(p => p.ConcurrencyToken).IsConcurrencyToken();

        builder.HasIndex(p => p.Key).IsUnique();

        // The catalogue query filters on product + visibility and orders by display order.
        builder.HasIndex(p => new { p.ProductId, p.IsVisible, p.DisplayOrder });

        // No navigation to Product: that entity lives in the shared catalogue, and a foreign key
        // from here to there would let the VPN module's model reach into it. The relationship is
        // enforced by the admin service, which checks the product exists before saving.
    }
}

public sealed class PlanAudienceRuleConfiguration : IEntityTypeConfiguration<PlanAudienceRule>
{
    public void Configure(EntityTypeBuilder<PlanAudienceRule> builder)
    {
        builder.ToTable("PlanAudienceRules");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Effect).HasConversion<int>().IsRequired();
        builder.Property(r => r.Kind).HasConversion<int>().IsRequired();
        builder.Property(r => r.Tier).HasConversion<int?>();
        builder.Property(r => r.RoleName).HasMaxLength(PlanAudienceRule.RoleNameMaxLength);
        builder.Property(r => r.Note).HasMaxLength(PlanAudienceRule.NoteMaxLength);

        builder.HasOne(r => r.Plan)
            .WithMany(p => p.AudienceRules)
            .HasForeignKey(r => r.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every read loads a plan's whole rule set, so this is the only access pattern.
        builder.HasIndex(r => r.PlanId);
    }
}
