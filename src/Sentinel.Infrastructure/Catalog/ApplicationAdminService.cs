using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Application.Media;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Products;
using Sentinel.Domain.Common;
using Sentinel.Infrastructure.Media;

namespace Sentinel.Infrastructure.Catalog;

public sealed class ApplicationAdminService : IApplicationAdminService
{
    private readonly ISentinelDbContext _db;
    private readonly IApplicationIconStorage _iconStorage;
    private readonly IAuditService _audit;
    private readonly MediaStorageOptions _mediaOptions;
    private readonly TimeProvider _timeProvider;

    public ApplicationAdminService(
        ISentinelDbContext db,
        IApplicationIconStorage iconStorage,
        IAuditService audit,
        IOptions<MediaStorageOptions> mediaOptions,
        TimeProvider timeProvider)
    {
        _db = db;
        _iconStorage = iconStorage;
        _audit = audit;
        _mediaOptions = mediaOptions.Value;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult<Guid>> CreateAsync(
        ApplicationSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(request.Key);

        if (!ApplicationKey.IsValid(key))
        {
            return OperationResult<Guid>.Failure(CatalogErrors.InvalidKey);
        }

        if (ValidateShape(request) is { } shapeFailure)
        {
            return OperationResult<Guid>.Failure(shapeFailure);
        }

        if (await ValidateCategoryAsync(request.CategoryId, cancellationToken) is { } categoryFailure)
        {
            return OperationResult<Guid>.Failure(categoryFailure);
        }

        if (await _db.Products.AnyAsync(a => a.Key == key, cancellationToken))
        {
            return OperationResult<Guid>.Failure(CatalogErrors.KeyTaken);
        }

        var application = new Product
        {
            Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
            Key = key,
        };

        Apply(application, request);
        _db.Products.Add(application);

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ApplicationCreated, nameof(Product), application.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("key", key)
                    .Set("releaseStatus", request.ReleaseStatus)
                    .Set("requiresEntitlement", request.RequiresExplicitEntitlement),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(application.Id);
    }

    public async Task<OperationResult> UpdateAsync(
        Guid id,
        ApplicationSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ValidateShape(request) is { } shapeFailure)
        {
            return OperationResult.Failure(shapeFailure);
        }

        if (await ValidateCategoryAsync(request.CategoryId, cancellationToken) is { } categoryFailure)
        {
            return OperationResult.Failure(categoryFailure);
        }

        var application = await _db.Products
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (application is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        if (request.ConcurrencyToken is { } token && application.ConcurrencyToken != token)
        {
            return OperationResult.Failure(OperationErrors.ConcurrencyConflict);
        }

        // The key is the stable identifier other systems and bookmarks use, so it is not
        // editable here — changing it would silently break every launch link already in use.
        var metadata = AuditMetadata.Create();

        if (application.ReleaseStatus != request.ReleaseStatus)
        {
            metadata.SetChange("releaseStatus", application.ReleaseStatus, request.ReleaseStatus);
        }

        if (application.IsEnabled != request.IsEnabled)
        {
            metadata.SetChange("isEnabled", application.IsEnabled, request.IsEnabled);
        }

        if (!string.Equals(application.LaunchUrl, request.LaunchUrl, StringComparison.Ordinal))
        {
            metadata.SetChange("launchUrl", application.LaunchUrl, request.LaunchUrl);
        }

        if (application.RequiresExplicitEntitlement != request.RequiresExplicitEntitlement)
        {
            metadata.SetChange(
                "requiresEntitlement",
                application.RequiresExplicitEntitlement,
                request.RequiresExplicitEntitlement);
        }

        if (application.MinimumTier != request.MinimumTier)
        {
            metadata.SetChange("minimumTier", application.MinimumTier, request.MinimumTier);
        }

        Apply(application, request);

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ApplicationUpdated, nameof(Product), id) with
            {
                Metadata = metadata,
            },
            cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult.Failure(OperationErrors.ConcurrencyConflict);
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> ReplaceIconAsync(
        Guid id,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken = default)
    {
        if (declaredLength <= 0)
        {
            return OperationResult.Failure(CatalogErrors.IconEmpty);
        }

        if (declaredLength > _mediaOptions.MaxIconBytes)
        {
            return OperationResult.Failure(CatalogErrors.IconTooLarge);
        }

        var application = await _db.Products
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (application is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        // The whole upload is buffered in memory first, bounded by the size limit checked
        // above. That keeps the byte-signature check and the write over the *same* bytes: a
        // sniff-then-stream approach reads the header, then copies from a stream the client
        // still controls, and a client can send different bytes the second time.
        using var buffer = new MemoryStream(capacity: (int)declaredLength);
        await content.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length == 0)
        {
            return OperationResult.Failure(CatalogErrors.IconEmpty);
        }

        if (buffer.Length > _mediaOptions.MaxIconBytes)
        {
            // The declared length was a lie; the actual bytes are what count.
            return OperationResult.Failure(CatalogErrors.IconTooLarge);
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        if (bytes.Length < ImageSignature.RequiredHeaderBytes)
        {
            return OperationResult.Failure(CatalogErrors.IconNotAnImage);
        }

        // Neither the file name nor the browser's content type is consulted. Only the bytes.
        var format = ImageSignature.Detect(bytes[..ImageSignature.RequiredHeaderBytes]);

        if (format == ImageFormat.Unknown)
        {
            return OperationResult.Failure(CatalogErrors.IconNotAnImage);
        }

        buffer.Position = 0;
        var stored = await _iconStorage.SaveAsync(buffer, format, cancellationToken);

        var previous = application.IconPath;
        application.IconPath = stored.StoredName;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ApplicationIconChanged, nameof(Product), id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("format", format)
                    .Set("bytes", stored.SizeInBytes),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        // Only after the new name is committed, so a failure here cannot leave the row
        // pointing at a file that no longer exists.
        if (!string.IsNullOrEmpty(previous))
        {
            await _iconStorage.DeleteAsync(previous, cancellationToken);
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveIconAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var application = await _db.Products
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (application is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        var previous = application.IconPath;

        if (string.IsNullOrEmpty(previous))
        {
            return OperationResult.Success();
        }

        application.IconPath = null;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ApplicationIconChanged, nameof(Product), id) with
            {
                Metadata = AuditMetadata.Create().Set("removed", true),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await _iconStorage.DeleteAsync(previous, cancellationToken);

        return OperationResult.Success();
    }

    private static void Apply(Product application, ApplicationSaveRequest request)
    {
        application.NameFa = request.NameFa.Trim();
        application.NameEn = request.NameEn.Trim();
        application.SummaryFa = Trim(request.SummaryFa);
        application.SummaryEn = Trim(request.SummaryEn);
        application.Type = request.Type;
        application.Capabilities = request.Capabilities;
        application.CategoryId = request.CategoryId;
        application.CurrentVersion = Trim(request.CurrentVersion);
        application.IsFeatured = request.IsFeatured;
        application.DescriptionFa = Trim(request.DescriptionFa);
        application.DescriptionEn = Trim(request.DescriptionEn);
        application.LaunchUrl = string.IsNullOrWhiteSpace(request.LaunchUrl)
            ? null
            : request.LaunchUrl.Trim();
        application.ReleaseStatus = request.ReleaseStatus;
        application.IsEnabled = request.IsEnabled;
        application.DisplayOrder = request.DisplayOrder;
        application.RequiresExplicitEntitlement = request.RequiresExplicitEntitlement;
        application.MinimumTier = request.MinimumTier;
    }

    /// <summary>
    /// Whether the product as described can actually work, independently of who is saving it.
    /// <para>
    /// Shared by create and update rather than duplicated, because a rule enforced on one path
    /// and not the other is the same as no rule.
    /// </para>
    /// </summary>
    private static string? ValidateShape(ApplicationSaveRequest request)
    {
        if (ValidateLaunchUrl(request.LaunchUrl) is { } urlFailure)
        {
            return urlFailure;
        }

        // A product that claims to be launchable but has nowhere to go would render a button
        // that dead-ends. Caught here so the operator finds out, not the member.
        if (request.Capabilities.Has(ProductCapability.Launchable)
            && string.IsNullOrWhiteSpace(request.LaunchUrl))
        {
            return CatalogErrors.LaunchUrlRequired;
        }

        return null;
    }

    /// <summary>
    /// Rejects a category that does not exist rather than letting the foreign key fail at save
    /// time — the operator gets a message instead of an error page.
    /// </summary>
    private async Task<string?> ValidateCategoryAsync(
        Guid? categoryId,
        CancellationToken cancellationToken)
    {
        if (categoryId is not { } id)
        {
            return null;
        }

        var exists = await _db.ProductCategories.AnyAsync(c => c.Id == id, cancellationToken);

        return exists ? null : CatalogErrors.CategoryNotFound;
    }

    /// <summary>
    /// The same policy the launch endpoint enforces, applied here so a bad destination is
    /// rejected at the point it is typed rather than discovered by a member.
    /// </summary>
    private static string? ValidateLaunchUrl(string? launchUrl)
    {
        // A product without a launch destination is legitimate — a download-only tool or a
        // subscription service has nowhere to "open". Only a supplied URL is checked.
        if (string.IsNullOrWhiteSpace(launchUrl))
        {
            return null;
        }

        return ValidateSuppliedLaunchUrl(launchUrl);
    }

    private static string? ValidateSuppliedLaunchUrl(string launchUrl) =>
        ApplicationUrlPolicy.Validate(launchUrl, out _) switch
        {
            ApplicationUrlRejection.None => null,
            ApplicationUrlRejection.InsecureScheme => CatalogErrors.InsecureLaunchUrl,
            _ => CatalogErrors.InvalidLaunchUrl,
        };

    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
