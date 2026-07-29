using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Notifications;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace Sentinel.Infrastructure.Notifications;

public sealed class TelegramNotificationChannel : INotificationChannel
{
    private readonly ITelegramBotClient? _client;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramNotificationChannel> _logger;

    public TelegramNotificationChannel(
        ITelegramClientProvider clientProvider,
        IOptions<TelegramOptions> options,
        ILogger<TelegramNotificationChannel> logger)
    {
        _client = clientProvider.Client;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _client is not null && _options.IsConfigured;

    public async Task<NotificationSendResult> SendAsync(
        long telegramUserId,
        string title,
        string body,
        string? absoluteLinkUrl,
        CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            return NotificationSendResult.Permanent("Telegram is not configured.");
        }

        var text = Compose(title, body, absoluteLinkUrl);

        try
        {
            await _client.SendMessage(
                chatId: telegramUserId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true },
                cancellationToken: cancellationToken);

            return NotificationSendResult.Success;
        }
        catch (ApiRequestException ex) when (IsPermanent(ex))
        {
            // The member blocked the bot, deleted the chat, or the id is no longer valid.
            // Retrying spends the rate budget to reach the same conclusion five more times.
            _logger.LogInformation(
                "Telegram refused a message permanently ({ErrorCode}); the portal copy remains.",
                ex.ErrorCode);

            return NotificationSendResult.Permanent($"telegram:{ex.ErrorCode}");
        }
        catch (ApiRequestException ex)
        {
            _logger.LogWarning("Telegram returned a retryable error ({ErrorCode}).", ex.ErrorCode);
            return NotificationSendResult.Transient($"telegram:{ex.ErrorCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach Telegram; will retry.");
            return NotificationSendResult.Transient("network");
        }
    }

    /// <summary>
    /// 400 and 403 mean the request will never succeed as sent — blocked bot, deactivated
    /// account, chat not found. 429 and 5xx are Telegram asking us to come back later.
    /// </summary>
    private static bool IsPermanent(ApiRequestException exception) =>
        exception.ErrorCode is 400 or 403;

    /// <summary>
    /// Builds the message body.
    /// <para>
    /// HTML parse mode is used for a little structure, so every interpolated value is escaped
    /// first. Titles and bodies can carry an administrator's free text and, later, remarks
    /// pulled from an external subscription — unescaped, a stray <c>&lt;</c> would break the
    /// message and Telegram would reject the whole send.
    /// </para>
    /// </summary>
    private static string Compose(string title, string body, string? absoluteLinkUrl)
    {
        var text = $"<b>{Escape(title)}</b>\n\n{Escape(body)}";

        if (!string.IsNullOrWhiteSpace(absoluteLinkUrl))
        {
            text += $"\n\n<a href=\"{Escape(absoluteLinkUrl)}\">{Escape(absoluteLinkUrl)}</a>";
        }

        return text;
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
