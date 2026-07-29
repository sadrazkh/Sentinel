using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Notifications;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Title).HasMaxLength(Notification.TitleMaxLength).IsRequired();
        builder.Property(n => n.Body).HasMaxLength(Notification.BodyMaxLength).IsRequired();
        builder.Property(n => n.LinkPath).HasMaxLength(512);
        builder.Property(n => n.LastFailureReason).HasMaxLength(Notification.FailureReasonMaxLength);

        builder.Property(n => n.Kind).HasConversion<int>().IsRequired();
        builder.Property(n => n.DeliveryState).HasConversion<int>().IsRequired();

        builder.HasOne(n => n.User)
            .WithMany(u => u.Notifications)
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The member's own list, newest first.
        builder.HasIndex(n => new { n.UserId, n.CreatedAt });

        // The unread badge, which every authenticated page renders.
        builder.HasIndex(n => new { n.UserId, n.ReadAt });

        // The outbox sweep: "what is still waiting to be delivered?"
        builder.HasIndex(n => new { n.DeliveryState, n.CreatedAt });
    }
}

public sealed class TelegramLinkTokenConfiguration : IEntityTypeConfiguration<TelegramLinkToken>
{
    public void Configure(EntityTypeBuilder<TelegramLinkToken> builder)
    {
        builder.ToTable("TelegramLinkTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash)
            .HasMaxLength(TelegramLinkToken.TokenHashLength)
            .IsRequired();

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redemption looks the token up by its hash, so this has to be the fast path.
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Sweeping expired tokens.
        builder.HasIndex(t => t.ExpiresAt);
    }
}
