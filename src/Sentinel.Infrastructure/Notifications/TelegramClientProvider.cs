using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Sentinel.Infrastructure.Notifications;

/// <summary>
/// Holds the bot client, or nothing when the integration is unconfigured.
/// <para>
/// A wrapper rather than registering a nullable <c>ITelegramBotClient</c> directly: the
/// container cannot express "this service is sometimes absent" without every consumer either
/// resolving optionally or the application failing to start over an optional integration.
/// One always-present provider with a nullable property keeps that decision in one place.
/// </para>
/// </summary>
public interface ITelegramClientProvider
{
    ITelegramBotClient? Client { get; }

    bool IsConfigured { get; }
}

public sealed class TelegramClientProvider : ITelegramClientProvider
{
    public TelegramClientProvider(IOptions<TelegramOptions> options)
    {
        var value = options.Value;

        IsConfigured = value.IsConfigured;
        Client = value.IsConfigured ? new TelegramBotClient(value.BotToken) : null;
    }

    public ITelegramBotClient? Client { get; }

    public bool IsConfigured { get; }
}
