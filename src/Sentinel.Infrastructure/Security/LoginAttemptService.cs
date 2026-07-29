using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Security;
using Sentinel.Domain.Common;
using Sentinel.Domain.Security;

namespace Sentinel.Infrastructure.Security;

public sealed class LoginAttemptService : ILoginAttemptService
{
    private readonly IDbContextFactory<Persistence.SentinelDbContext> _dbFactory;
    private readonly ISentinelDbContext _db;
    private readonly IClientContext _clientContext;
    private readonly TimeProvider _timeProvider;

    public LoginAttemptService(
        IDbContextFactory<Persistence.SentinelDbContext> dbFactory,
        ISentinelDbContext db,
        IClientContext clientContext,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _db = db;
        _clientContext = clientContext;
        _timeProvider = timeProvider;
    }

    public async Task RecordAsync(
        string identifier,
        Guid? userId,
        bool succeeded,
        LoginFailureReason failureReason,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var attempt = new LoginAttempt
        {
            Id = SequentialGuid.New(now),
            AttemptedIdentifier = Normalize(identifier),
            UserId = userId,
            Succeeded = succeeded,
            FailureReason = failureReason,
            IpAddress = Truncate(_clientContext.IpAddress, LoginAttempt.IpAddressMaxLength),
            UserAgent = Truncate(_clientContext.UserAgent, LoginAttempt.UserAgentMaxLength),
            OccurredAt = now,
        };

        // Its own unit of work: the attempt must be recorded whether or not the surrounding
        // sign-in flow goes on to commit anything.
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.LoginAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LoginAttemptView>> GetRecentForUserAsync(
        Guid userId,
        int take,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 200);

        return await _db.LoginAttempts
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.OccurredAt)
            .Take(take)
            .Select(a => new LoginAttemptView(
                a.OccurredAt,
                a.Succeeded,
                a.FailureReason,
                a.IpAddress,
                a.UserAgent))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Trims and lower-cases the identifier and caps its length. Storing an unbounded raw
    /// value would let a client push arbitrary data into the table.
    /// </summary>
    private static string Normalize(string identifier)
    {
        var trimmed = (identifier ?? string.Empty).Trim().ToLowerInvariant();
        return trimmed.Length <= LoginAttempt.IdentifierMaxLength
            ? trimmed
            : trimmed[..LoginAttempt.IdentifierMaxLength];
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
