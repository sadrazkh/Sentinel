using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Entitlements;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Notifications;
using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;

namespace Sentinel.Infrastructure.Entitlements;

public sealed class EntitlementAdminService : IEntitlementAdminService
{
    private readonly ISentinelDbContext _db;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;
    private readonly INotificationLocalizer _localizer;
    private readonly IClientContext _clientContext;
    private readonly TimeProvider _timeProvider;

    public EntitlementAdminService(
        ISentinelDbContext db,
        IAuditService audit,
        INotificationService notifications,
        INotificationLocalizer localizer,
        IClientContext clientContext,
        TimeProvider timeProvider)
    {
        _db = db;
        _audit = audit;
        _notifications = notifications;
        _localizer = localizer;
        _clientContext = clientContext;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult> GrantAsync(
        Guid userId,
        Guid productId,
        GrantEntitlementRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var startsAt = request.StartsAt ?? now;

        if (request.ExpiresAt is { } expiresAt && expiresAt < startsAt)
        {
            return OperationResult.Failure(OperationErrors.InvalidDateRange);
        }

        if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        if (!await _db.Products.AnyAsync(a => a.Id == productId, cancellationToken))
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        var entitlement = await _db.ProductEntitlements
            .FirstOrDefaultAsync(e => e.UserId == userId && e.ProductId == productId, cancellationToken);

        var isNew = entitlement is null;

        if (isNew)
        {
            entitlement = new ProductEntitlement
            {
                Id = SequentialGuid.New(now),
                UserId = userId,
                ProductId = productId,
            };

            _db.ProductEntitlements.Add(entitlement);
        }
        else if (request.ConcurrencyToken is { } token && entitlement!.ConcurrencyToken != token)
        {
            return OperationResult.Failure(OperationErrors.ConcurrencyConflict);
        }

        var wasRevoked = entitlement!.RevokedAt is not null;

        entitlement.IsEnabled = true;
        entitlement.StartsAt = startsAt;
        entitlement.ExpiresAt = request.ExpiresAt;
        entitlement.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        entitlement.GrantedBy = _clientContext.UserId;

        // Re-granting clears the revocation rather than adding a second row: there is exactly
        // one row per (user, application), so the access check stays a single lookup and two
        // rows can never disagree about the answer.
        entitlement.RevokedAt = null;
        entitlement.RevokedBy = null;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.EntitlementGranted, nameof(ProductEntitlement), userId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("productId", productId)
                    .Set("startsAt", startsAt)
                    .Set("expiresAt", request.ExpiresAt)
                    .Set("reinstated", wasRevoked),
            },
            cancellationToken);

        // Staged on the same unit of work as the grant, so the member is never told about
        // access that then failed to save — nor left unaware of access that did.
        var application = await _db.Products
            .Where(a => a.Id == productId)
            .Select(a => new { a.NameFa, a.NameEn })
            .FirstOrDefaultAsync(cancellationToken);

        var recipientCulture = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.PreferredCulture)
            .FirstOrDefaultAsync(cancellationToken);

        // Written in the recipient's language, including which name of the application to use.
        var isPersian = recipientCulture is null
                        || recipientCulture.StartsWith("fa", StringComparison.OrdinalIgnoreCase);

        var applicationName = (isPersian ? application?.NameFa : application?.NameEn)
                              ?? application?.NameEn
                              ?? "—";

        await _notifications.CreateAsync(
            userId,
            new NewNotification(
                NotificationKind.Entitlement,
                _localizer.Get("notice.entitlementGranted.title", recipientCulture),
                _localizer.Get("notice.entitlementGranted.body", recipientCulture, applicationName),
                LinkPath: "/Apps"),
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

    public async Task<OperationResult> RevokeAsync(
        Guid userId,
        Guid productId,
        string? notes,
        Guid? concurrencyToken,
        CancellationToken cancellationToken = default)
    {
        var entitlement = await _db.ProductEntitlements
            .FirstOrDefaultAsync(e => e.UserId == userId && e.ProductId == productId, cancellationToken);

        if (entitlement is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        if (concurrencyToken is { } token && entitlement.ConcurrencyToken != token)
        {
            return OperationResult.Failure(OperationErrors.ConcurrencyConflict);
        }

        if (entitlement.RevokedAt is not null)
        {
            // Already revoked. Reporting success keeps a double-click idempotent instead of
            // showing an error for a state the operator already wanted.
            return OperationResult.Success();
        }

        entitlement.RevokedAt = _timeProvider.GetUtcNow();
        entitlement.RevokedBy = _clientContext.UserId;

        if (!string.IsNullOrWhiteSpace(notes))
        {
            entitlement.Notes = notes.Trim();
        }

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.EntitlementRevoked, nameof(ProductEntitlement), userId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("productId", productId)
                    .Set("notes", entitlement.Notes),
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
}
