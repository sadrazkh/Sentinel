using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Billing;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("Wallets");

        builder.HasKey(wallet => wallet.Id);

        builder.Property(wallet => wallet.Currency).HasMaxLength(3).IsRequired();
        builder.Property(wallet => wallet.FrozenReason).HasMaxLength(500);

        // Every movement writes through this. It is what makes "check the balance, then subtract"
        // a single decision rather than two steps with a gap between them.
        builder.Property(wallet => wallet.ConcurrencyToken).IsConcurrencyToken();

        // One wallet per member, enforced by the database. Two would each hold part of a balance,
        // and which one a query found would decide whether somebody could afford something.
        builder.HasIndex(wallet => wallet.UserId).IsUnique();
    }
}

public sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.ToTable("WalletTransactions");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Kind).HasConversion<int>().IsRequired();
        builder.Property(entry => entry.Direction).HasConversion<int>().IsRequired();
        builder.Property(entry => entry.Currency).HasMaxLength(3).IsRequired();
        builder.Property(entry => entry.Description)
            .HasMaxLength(WalletTransaction.DescriptionMaxLength);
        builder.Property(entry => entry.Reference)
            .HasMaxLength(WalletTransaction.ReferenceMaxLength);

        // No concurrency token, and none is missing: nothing ever updates one of these rows. The
        // ledger is append-only, and a correction is a new row that points at the old one.

        builder.HasOne(entry => entry.Wallet)
            .WithMany()
            .HasForeignKey(entry => entry.WalletId)

            // Restrict, not Cascade. Deleting a wallet must not take its history with it — and in
            // practice this means a wallet with any history cannot be deleted at all, which is the
            // intended answer.
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(entry => entry.ReversesTransaction)
            .WithMany()
            .HasForeignKey(entry => entry.ReversesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Idempotency, enforced by the database rather than by a prior read. Two replicas racing
        // with the same key both find nothing and both insert; this is what stops the second.
        //
        // No filter is needed for the entries that carry no key: both engines this portal supports
        // treat NULLs as distinct in a unique index, so any number of them coexist.
        builder.HasIndex(entry => new { entry.WalletId, entry.Reference }).IsUnique();

        // An entry can be reversed once. A second reversal of the same row would double the
        // correction, which is how a ledger ends up further from the truth than before somebody
        // tried to fix it.
        builder.HasIndex(entry => entry.ReversesTransactionId).IsUnique();

        // The member's own statement, newest first.
        builder.HasIndex(entry => new { entry.UserId, entry.CreatedAt });

        builder.HasIndex(entry => entry.WalletId);
    }
}
