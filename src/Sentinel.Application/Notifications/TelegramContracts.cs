using Sentinel.Application.Common;

namespace Sentinel.Application.Notifications;

public sealed record TelegramLinkState(
    bool IsConfigured,
    bool IsLinked,
    string? TelegramUsername,
    DateTimeOffset? LinkedAt,
    bool NotificationsEnabled,
    string? BotUsername);

public sealed record TelegramLinkInvitation(string DeepLink, DateTimeOffset ExpiresAt);

public interface ITelegramLinkService
{
    Task<TelegramLinkState> GetStateAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a fresh single-use token and returns the deep link to open in Telegram.
    /// Any earlier unused token for the same member is invalidated, so only the most recent
    /// link works.
    /// </summary>
    Task<OperationResult<TelegramLinkInvitation>> CreateInvitationAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a token presented by a Telegram account. Called from the bot, where the only
    /// trustworthy inputs are the token and the numeric Telegram user id.
    /// </summary>
    Task<OperationResult> RedeemAsync(
        string token,
        long telegramUserId,
        string? telegramUsername,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UnlinkAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<OperationResult> SetNotificationsEnabledAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves the portal account behind a Telegram id, for bot commands.</summary>
    Task<Guid?> FindUserIdByTelegramIdAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends one already-composed message. Implemented by the Telegram client; kept as an
/// interface so the delivery service can be tested without a bot token or a network.
/// </summary>
public interface INotificationChannel
{
    bool IsConfigured { get; }

    Task<NotificationSendResult> SendAsync(
        long telegramUserId,
        string title,
        string body,
        string? absoluteLinkUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IsPermanent"/> separates "this will never work" — the member blocked the bot, the
/// chat is gone — from a transient failure worth retrying. Retrying a permanent failure five
/// times just delays the inevitable and spends the bot's rate budget.
/// </summary>
public sealed record NotificationSendResult(bool Succeeded, bool IsPermanent, string? FailureReason)
{
    public static readonly NotificationSendResult Success = new(true, false, null);

    public static NotificationSendResult Permanent(string reason) => new(false, true, reason);

    public static NotificationSendResult Transient(string reason) => new(false, false, reason);
}

public static class TelegramErrors
{
    public const string NotConfigured = "telegram.error.notConfigured";
    public const string InvalidToken = "telegram.error.invalidToken";
    public const string AlreadyLinkedToAnotherAccount = "telegram.error.alreadyLinkedElsewhere";
    public const string NotLinked = "telegram.error.notLinked";
}
