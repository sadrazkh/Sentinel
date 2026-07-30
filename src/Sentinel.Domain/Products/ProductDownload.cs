using Sentinel.Domain.Common;

namespace Sentinel.Domain.Products;

public enum DownloadPlatform
{
    Any = 0,
    Windows = 1,
    MacOs = 2,
    Linux = 3,
    Android = 4,
    Ios = 5,
    Web = 6,
}

/// <summary>
/// A file or client application a member can obtain for a product — the VPN client apps, a
/// desktop installer, a configuration bundle.
/// <para>
/// The portal stores a URL rather than the bytes: these are third-party client applications
/// published elsewhere, and mirroring them would mean taking responsibility for distributing
/// binaries we did not build. The download therefore goes through a portal endpoint that checks
/// access and then redirects — the same shape as launching an application.
/// </para>
/// </summary>
public class ProductDownload : IConcurrencyAware, ITimestamped
{
    public const int TitleMaxLength = 160;
    public const int NoteMaxLength = 500;
    public const int UrlMaxLength = 2048;
    public const int VersionMaxLength = 32;
    public const int ChecksumMaxLength = 128;

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public DownloadPlatform Platform { get; set; } = DownloadPlatform.Any;

    public ContentVisibility Visibility { get; set; } = ContentVisibility.Entitled;

    public string TitleFa { get; set; } = string.Empty;

    public string TitleEn { get; set; } = string.Empty;

    public string? NoteFa { get; set; }

    public string? NoteEn { get; set; }

    /// <summary>
    /// Validated against the URL policy — HTTPS only, no credentials, no internal host — both
    /// when stored and again before any redirect.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    public string? Version { get; set; }

    /// <summary>
    /// Displayed so a member can verify what they downloaded. Never checked by us: we do not
    /// hold the bytes, so claiming to have verified them would be a lie.
    /// </summary>
    public string? Checksum { get; set; }

    /// <summary>Reported size in bytes, for the label only.</summary>
    public long? SizeBytes { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
