using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Memberships;
using Sentinel.Application.Users;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Memberships;

namespace Sentinel.Infrastructure.Memberships;

public sealed class MembershipAdminService : IMembershipAdminService
{
    private readonly ISentinelDbContext _db;
    private readonly IMembershipStatusResolver _resolver;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;

    public MembershipAdminService(
        ISentinelDbContext db,
        IMembershipStatusResolver resolver,
        IAuditService audit,
        TimeProvider timeProvider)
    {
        _db = db;
        _resolver = resolver;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public MembershipSnapshot Preview(MembershipEditRequest request) =>
        _resolver.Resolve(ToFacts(request), _timeProvider.GetUtcNow());

    public async Task<OperationResult> SaveAsync(
        Guid userId,
        MembershipEditRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.EndsAt is { } endsAt && endsAt < request.StartsAt)
        {
            return OperationResult.Failure(OperationErrors.InvalidDateRange);
        }

        if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        var membership = await _db.Memberships
            .FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);

        var isNew = membership is null;

        if (isNew)
        {
            membership = new Membership
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
                UserId = userId,
            };

            _db.Memberships.Add(membership);
        }
        else if (request.ConcurrencyToken is { } token && membership!.ConcurrencyToken != token)
        {
            // The form was rendered from an older version of this row. Refusing is the whole
            // point: silently applying the edit would erase whatever changed in between.
            return OperationResult.Failure(OperationErrors.ConcurrencyConflict);
        }

        var before = isNew
            ? null
            : new MembershipFacts(
                membership!.Tier,
                membership.AdminState,
                membership.StartsAt,
                membership.EndsAt,
                membership.GracePeriodDaysOverride);

        membership!.Tier = request.Tier;
        membership.AdminState = request.AdminState;
        membership.StartsAt = request.StartsAt;
        membership.EndsAt = request.EndsAt;
        membership.GracePeriodDaysOverride = request.GracePeriodDaysOverride;
        membership.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();

        var metadata = AuditMetadata.Create();

        if (before is null)
        {
            metadata
                .Set("tier", request.Tier)
                .Set("adminState", request.AdminState)
                .Set("startsAt", request.StartsAt)
                .Set("endsAt", request.EndsAt);
        }
        else
        {
            if (before.Tier != request.Tier)
            {
                metadata.SetChange("tier", before.Tier, request.Tier);
            }

            if (before.AdminState != request.AdminState)
            {
                metadata.SetChange("adminState", before.AdminState, request.AdminState);
            }

            if (before.StartsAt != request.StartsAt)
            {
                metadata.SetChange("startsAt", before.StartsAt, request.StartsAt);
            }

            if (before.EndsAt != request.EndsAt)
            {
                metadata.SetChange("endsAt", before.EndsAt, request.EndsAt);
            }

            if (before.GracePeriodDaysOverride != request.GracePeriodDaysOverride)
            {
                metadata.SetChange(
                    "graceOverride", before.GracePeriodDaysOverride, request.GracePeriodDaysOverride);
            }
        }

        await _audit.RecordAsync(
            AuditEntry.For(
                isNew ? AuditActions.MembershipCreated : AuditActions.MembershipUpdated,
                nameof(Membership),
                userId) with
            {
                Metadata = metadata,
            },
            cancellationToken);

        try
        {
            // The audit row and the change commit together: an audited operation and its
            // record either both land or neither does.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer got there between the load and the save.
            return OperationResult.Failure(OperationErrors.ConcurrencyConflict);
        }

        return OperationResult.Success();
    }

    private static MembershipFacts ToFacts(MembershipEditRequest request) => new(
        request.Tier,
        request.AdminState,
        request.StartsAt,
        request.EndsAt,
        request.GracePeriodDaysOverride);
}
