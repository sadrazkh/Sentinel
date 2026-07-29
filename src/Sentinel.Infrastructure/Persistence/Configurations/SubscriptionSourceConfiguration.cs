using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Subscriptions;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionSourceConfiguration : IEntityTypeConfiguration<SubscriptionSource>
{
    public void Configure(EntityTypeBuilder<SubscriptionSource> builder)
    {
        builder.ToTable("SubscriptionSources");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Title).HasMaxLength(SubscriptionSource.TitleMaxLength).IsRequired();
        builder.Property(s => s.Url).HasMaxLength(SubscriptionSource.UrlMaxLength).IsRequired();
        builder.Property(s => s.Notes).HasMaxLength(SubscriptionSource.NotesMaxLength);
        builder.Property(s => s.LastFetchError).HasMaxLength(SubscriptionSource.ErrorMaxLength);
        builder.Property(s => s.LastFetchStatus).HasConversion<int>().IsRequired();

        builder.Property(s => s.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The member's own list.
        builder.HasIndex(s => new { s.UserId, s.CreatedAt });

        // The admin sweep for dead sources.
        builder.HasIndex(s => s.ExpiresAt);
        builder.HasIndex(s => s.LastFetchStatus);

        // One member cannot add the same link twice, which would double every card on their
        // page and double the outbound fetches.
        builder.HasIndex(s => new { s.UserId, s.Url }).IsUnique();
    }
}
