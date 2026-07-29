using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;

namespace Sentinel.Infrastructure.Notifications;

public sealed class TelegramLinkService : ITelegramLinkService
{
    /// <summary>
    /// 32 bytes of randomness, base64url-encoded. Telegram's <c>start</c> parameter allows
    /// A–Z, a–z, 0–9, underscore and hyphen — exactly the base64url alphabet — and 256 bits is
    /// far past guessable for a value that lives ten minutes.
    /// </summary>
    private const int TokenByteLength = 32;

    private readonly ISentinelDbContext _db;
    private readonly IDbContextFactory<Persistence.SentinelDbContext> _dbFactory;
    private readonly IAuditService _audit;
    private readonly INotificationLocalizer _localizer;
    private readonly TelegramOptions _options;
    private readonly TimeProvider _timeProvider;

    public TelegramLinkService(
        ISentinelDbContext db,
        IDbContextFactory<Persistence.SentinelDbContext> dbFactory,
        IAuditService audit,
        INotificationLocalizer localizer,
        IOptions<TelegramOptions> options,
        TimeProvider timeProvider)
    {
        _db = db;
        _dbFactory = dbFactory;
        _audit = audit;
        _localizer = localizer;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<TelegramLinkState> GetStateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.TelegramUserId,
                u.TelegramUsername,
                u.TelegramLinkedAt,
                u.TelegramNotificationsEnabled,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new TelegramLinkState(
            _options.IsConfigured,
            user?.TelegramUserId is not null,
            user?.TelegramUsername,
            user?.TelegramLinkedAt,
            user?.TelegramNotificationsEnabled ?? true,
            _options.IsConfigured ? _options.BotUsername : null);
    }

    public async Task<OperationResult<TelegramLinkInvitation>> CreateInvitationAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return OperationResult<TelegramLinkInvitation>.Failure(TelegramErrors.NotConfigured);
        }

        if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return OperationResult<TelegramLinkInvitation>.Failure(OperationErrors.NotFound);
        }

        var now = _timeProvider.GetUtcNow();

        // Any earlier unused token is retired first, so a link that was generated, abandoned,
        // and left sitting in a chat history stops working the moment a new one is issued.
        await _db.TelegramLinkTokens
            .Where(t => t.UserId == userId && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ExpiresAt, now), cancellationToken);

        var token = GenerateToken();

        _db.TelegramLinkTokens.Add(new TelegramLinkToken
        {
            Id = SequentialGuid.New(now),
            UserId = userId,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now.Add(TelegramLinkToken.Lifetime),
        });

        await _db.SaveChangesAsync(cancellationToken);

        var deepLink = $"https://t.me/{_options.BotUsername}?start={token}";

        return OperationResult<TelegramLinkInvitation>.Success(
            new TelegramLinkInvitation(deepLink, now.Add(TelegramLinkToken.Lifetime)));
    }

    public async Task<OperationResult> RedeemAsync(
        string token,
        long telegramUserId,
        string? telegramUsername,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || telegramUserId <= 0)
        {
            return OperationResult.Failure(TelegramErrors.InvalidToken);
        }

        // Its own context: this runs from the bot's polling loop, which has no request scope.
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var hash = Hash(token);

        var link = await db.TelegramLinkTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        // Unknown, already used and expired all give the same answer. A bot that said
        // "that token expired" would confirm the token had once been real.
        if (link is null || !link.IsUsableAt(now))
        {
            return OperationResult.Failure(TelegramErrors.InvalidToken);
        }

        var alreadyLinked = await db.Users.AnyAsync(
            u => u.TelegramUserId == telegramUserId && u.Id != link.UserId, cancellationToken);

        if (alreadyLinked)
        {
            // One Telegram account cannot serve two portal accounts, or one chat would receive
            // two members' notifications.
            return OperationResult.Failure(TelegramErrors.AlreadyLinkedToAnotherAccount);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == link.UserId, cancellationToken);

        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        link.ConsumedAt = now;
        link.ConsumedByTelegramUserId = telegramUserId;

        user.TelegramUserId = telegramUserId;
        user.TelegramUsername = Truncate(telegramUsername, ApplicationUser.TelegramUsernameMaxLength);
        user.TelegramLinkedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            Id = SequentialGuid.New(now),
            ActorUserId = user.Id,
            ActorUserName = user.UserName,
            Action = AuditActions.TelegramLinked,
            EntityType = nameof(ApplicationUser),
            EntityId = user.Id.ToString(),
            OccurredAt = now,
            Result = AuditResult.Success,

            // The numeric id is recorded; the token never is.
            MetadataJson = AuditMetadata.Create()
                .Set("telegramUserId", telegramUserId)
                .ToJson(),
        });

        db.Notifications.Add(new Notification
        {
            Id = SequentialGuid.New(now),
            UserId = user.Id,
            Kind = NotificationKind.Security,
            Title = _localizer.Get("notice.telegramLinked.title", user.PreferredCulture),
            Body = _localizer.Get("notice.telegramLinked.body", user.PreferredCulture),
            CreatedAt = now,
            DeliveryState = NotificationDeliveryState.Pending,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index on TelegramUserId is the real arbiter if two redemptions race.
            return OperationResult.Failure(TelegramErrors.AlreadyLinkedToAnotherAccount);
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> UnlinkAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        if (user.TelegramUserId is null)
        {
            return OperationResult.Failure(TelegramErrors.NotLinked);
        }

        var previous = user.TelegramUserId;

        user.TelegramUserId = null;
        user.TelegramUsername = null;
        user.TelegramLinkedAt = null;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.TelegramUnlinked, nameof(ApplicationUser), userId) with
            {
                Metadata = AuditMetadata.Create().Set("telegramUserId", previous),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SetNotificationsEnabledAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var updated = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(u => u.TelegramNotificationsEnabled, enabled),
                cancellationToken);

        return updated > 0
            ? OperationResult.Success()
            : OperationResult.Failure(OperationErrors.NotFound);
    }

    public async Task<Guid?> FindUserIdByTelegramIdAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Users
            .AsNoTracking()
            .Where(u => u.TelegramUserId == telegramUserId)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    /// <summary>
    /// SHA-256 is right here where it would be wrong for a password: the input is 256 bits of
    /// entropy this application generated, so there is no dictionary to run against it, and the
    /// lookup happens on the bot's hot path.
    /// </summary>
    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
