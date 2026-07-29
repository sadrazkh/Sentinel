using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Notifications;

/// <summary>
/// A single-use token that proves the person who typed <c>/start</c> in Telegram is the same
/// person signed in to the portal.
/// <para>
/// Only a hash is stored. The token itself travels through a Telegram deep link, which means
/// it passes through Telegram's servers and lands in a chat history — so it is treated like
/// any other credential in transit: short-lived, single-use, and not recoverable from our
/// database if that database is ever read.
/// </para>
/// </summary>
public class TelegramLinkToken
{
    public const int TokenHashLength = 64;

    /// <summary>
    /// Short on purpose. The window between generating the link and pressing it in Telegram is
    /// seconds; anything longer is just a larger target.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    /// <summary>Lower-case hex SHA-256 of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>The Telegram account that redeemed it, for the audit trail.</summary>
    public long? ConsumedByTelegramUserId { get; set; }

    public bool IsUsableAt(DateTimeOffset instant) => ConsumedAt is null && ExpiresAt > instant;
}
