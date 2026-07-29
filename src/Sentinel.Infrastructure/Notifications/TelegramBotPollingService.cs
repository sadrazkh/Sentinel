using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Notifications;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Sentinel.Infrastructure.Notifications;

/// <summary>
/// Listens for bot commands, which in this version means one thing: redeeming a link token.
/// <para>
/// Long polling rather than a webhook. A webhook is cheaper at scale but needs a publicly
/// reachable inbound route and a secret path to authenticate Telegram's calls; polling works
/// identically on a laptop, behind CapRover, and in a container with no ingress, which is worth
/// more than the saved requests at this size.
/// </para>
/// <para>
/// Everything arriving here is untrusted. The only inputs used are the numeric sender id, which
/// Telegram guarantees, and the token text, which is verified against a hash before it means
/// anything.
/// </para>
/// </summary>
public sealed class TelegramBotPollingService : BackgroundService
{
    private const string StartCommand = "/start";

    private readonly ITelegramBotClient? _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBotPollingService> _logger;

    public TelegramBotPollingService(
        ITelegramClientProvider clientProvider,
        IServiceScopeFactory scopeFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramBotPollingService> logger)
    {
        _client = clientProvider.Client;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_client is null || !_options.IsConfigured || !_options.UsePolling)
        {
            _logger.LogInformation("Telegram polling is off.");
            return;
        }

        var receiverOptions = new ReceiverOptions
        {
            // Only messages are of interest. Asking for fewer update types means Telegram
            // sends less and there is less untrusted input to reason about.
            AllowedUpdates = [UpdateType.Message],

            // Anything queued while the portal was down is stale by definition — a link token
            // has a ten-minute life — and replaying it would only produce confusing replies.
            DropPendingUpdates = true,
        };

        _logger.LogInformation("Telegram polling started for @{BotUsername}.", _options.BotUsername);

        await _client.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient client,
        Update update,
        CancellationToken cancellationToken)
    {
        if (update.Message is not { Text: { } text, From: { } sender } message)
        {
            return;
        }

        // Bots talking to bots is not a flow this portal has.
        if (sender.IsBot)
        {
            return;
        }

        try
        {
            var reply = await HandleCommandAsync(text, sender, cancellationToken);

            if (reply is not null)
            {
                await client.SendMessage(message.Chat.Id, reply, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // A single malformed message must not stop the receiver. The reply is deliberately
            // vague: an error string echoed into a chat is an information leak.
            _logger.LogError(ex, "Failed to handle a Telegram update.");
        }
    }

    private async Task<string?> HandleCommandAsync(
        string text,
        User sender,
        CancellationToken cancellationToken)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith(StartCommand, StringComparison.OrdinalIgnoreCase))
        {
            return "Open your portal profile and choose “Connect Telegram” to link this chat.";
        }

        var payload = trimmed[StartCommand.Length..].Trim();

        if (payload.Length == 0)
        {
            return "Open your portal profile and choose “Connect Telegram” to link this chat.";
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var linkService = scope.ServiceProvider.GetRequiredService<ITelegramLinkService>();

        var result = await linkService.RedeemAsync(
            payload, sender.Id, sender.Username, cancellationToken);

        if (result.Succeeded)
        {
            return "Connected. Portal notifications will arrive here from now on.";
        }

        // The same reply for an unknown, used, or expired token: distinguishing them would
        // confirm to a stranger that a token had once been real.
        return result.ErrorKey == TelegramErrors.AlreadyLinkedToAnotherAccount
            ? "This Telegram account is already linked to a different portal account."
            : "That link is not valid or has expired. Generate a new one from your portal profile.";
    }

    private Task HandleErrorAsync(
        ITelegramBotClient client,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The receiver recovers on its own; this only records that it happened. The exception
        // is logged without the bot token ever appearing in it.
        _logger.LogWarning(exception, "Telegram polling error.");
        return Task.CompletedTask;
    }
}
