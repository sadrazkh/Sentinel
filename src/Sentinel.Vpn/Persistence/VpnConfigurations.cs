using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Persistence;

/// <summary>
/// A marker for this assembly, so the host can register the VPN entity configurations without
/// naming each one — and so the shared DbContext does not have to know what they are.
/// </summary>
public static class VpnModelMarker
{
    public static readonly System.Reflection.Assembly Assembly = typeof(VpnModelMarker).Assembly;
}

public sealed class VpnServerConfiguration : IEntityTypeConfiguration<VpnServer>
{
    public void Configure(EntityTypeBuilder<VpnServer> builder)
    {
        builder.ToTable("VpnServers");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Key).HasMaxLength(VpnServer.KeyMaxLength).IsRequired();
        builder.Property(s => s.NameFa).HasMaxLength(VpnServer.NameMaxLength).IsRequired();
        builder.Property(s => s.NameEn).HasMaxLength(VpnServer.NameMaxLength).IsRequired();
        builder.Property(s => s.CountryCode).HasMaxLength(VpnServer.CountryCodeMaxLength).IsRequired();
        builder.Property(s => s.BaseUrl).HasMaxLength(VpnServer.BaseUrlMaxLength).IsRequired();
        builder.Property(s => s.Notes).HasMaxLength(VpnServer.NotesMaxLength);

        // Ciphertext, so generous: the data-protection payload is several times the token.
        builder.Property(s => s.EncryptedApiToken).HasMaxLength(2048).IsRequired();
        builder.Property(s => s.ApiTokenHint).HasMaxLength(16);

        builder.Property(s => s.LastHealthError).HasMaxLength(500);

        builder.Property(s => s.Status).HasConversion<int>().IsRequired();
        builder.Property(s => s.Health).HasConversion<int>().IsRequired();

        builder.Property(s => s.ConcurrencyToken).IsConcurrencyToken();

        builder.HasIndex(s => s.Key).IsUnique();

        // Server selection filters on these three and orders by priority.
        builder.HasIndex(s => new { s.Status, s.CountryCode, s.SelectionPriority });
    }
}

public sealed class ServerInboundProfileConfiguration : IEntityTypeConfiguration<ServerInboundProfile>
{
    public void Configure(EntityTypeBuilder<ServerInboundProfile> builder)
    {
        builder.ToTable("ServerInboundProfiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Label).HasMaxLength(ServerInboundProfile.LabelMaxLength).IsRequired();
        builder.Property(p => p.Protocol).HasMaxLength(ServerInboundProfile.ProtocolMaxLength).IsRequired();
        builder.Property(p => p.Remark).HasMaxLength(ServerInboundProfile.RemarkMaxLength);

        builder.Property(p => p.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(p => p.Server)
            .WithMany(s => s.InboundProfiles)
            .HasForeignKey(p => p.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        // One profile per (server, inbound): two rows for the same panel inbound would let the
        // portal attach a client twice and then disagree with itself about capacity.
        builder.HasIndex(p => new { p.ServerId, p.InboundId }).IsUnique();
    }
}
