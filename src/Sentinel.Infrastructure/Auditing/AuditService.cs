using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Auditing;

public sealed class AuditService : IAuditService
{
    private readonly ISentinelDbContext _db;
    private readonly IDbContextFactory<SentinelDbContext> _dbFactory;
    private readonly IClientContext _clientContext;
    private readonly TimeProvider _timeProvider;

    public AuditService(
        ISentinelDbContext db,
        IDbContextFactory<SentinelDbContext> dbFactory,
        IClientContext clientContext,
        TimeProvider timeProvider)
    {
        _db = db;
        _dbFactory = dbFactory;
        _clientContext = clientContext;
        _timeProvider = timeProvider;
    }

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        _db.AuditLogs.Add(Build(entry));
        return Task.CompletedTask;
    }

    public async Task RecordAndSaveAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        // A dedicated context, so writing an audit row never drags along unrelated pending
        // changes that the caller has not decided to commit yet.
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditLogs.Add(Build(entry));
        await db.SaveChangesAsync(cancellationToken);
    }

    private AuditLog Build(AuditEntry entry) => new()
    {
        Id = SequentialGuid.New(),
        ActorUserId = entry.ActorUserIdOverride ?? _clientContext.UserId,
        ActorUserName = Truncate(
            entry.ActorUserNameOverride ?? _clientContext.UserName,
            AuditLog.ActorNameMaxLength),
        Action = entry.Action,
        EntityType = entry.EntityType,
        EntityId = Truncate(entry.EntityId, AuditLog.EntityIdMaxLength),
        OccurredAt = _timeProvider.GetUtcNow(),
        IpAddress = Truncate(_clientContext.IpAddress, AuditLog.IpAddressMaxLength),
        UserAgent = Truncate(_clientContext.UserAgent, AuditLog.UserAgentMaxLength),
        Result = entry.Result,
        CorrelationId = Truncate(_clientContext.CorrelationId, AuditLog.CorrelationIdMaxLength),
        MetadataJson = Truncate(entry.Metadata?.ToJson(), AuditLog.MetadataMaxLength),
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
