using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Notifications;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The link flow, exercised through the real service.
/// <para>
/// The bot itself is not started — these tests call <c>RedeemAsync</c> the way the polling loop
/// would, which is where the security decisions actually live. Starting a receiver would add a
/// network dependency and test Telegram's library rather than this application's rules.
/// </para>
/// </summary>
public sealed class TelegramLinkTests : IClassFixture<TelegramLinkTests.TelegramEnabledFactory>
{
    /// <summary>
    /// Configured well enough to issue and redeem tokens. The token is deliberately fake: no
    /// test here talks to Telegram, and a real one in a repository would be a leaked credential.
    /// </summary>
    public sealed class TelegramEnabledFactory : SentinelWebApplicationFactory
    {
        protected override void ConfigureTestSettings(IWebHostBuilder builder)
        {
            builder.UseSetting("Telegram:Enabled", "true");
            builder.UseSetting("Telegram:BotToken", "000000:integration-test-token-not-real");
            builder.UseSetting("Telegram:BotUsername", "SentinelTestBot");
            builder.UseSetting("Telegram:PublicBaseUrl", "https://portal.invalid");

            // No receiver and no delivery loop: this suite covers the linking rules, and a
            // polling loop would try to reach api.telegram.org from a unit-test host.
            builder.UseSetting("Telegram:UsePolling", "false");
        }
    }

    private readonly TelegramEnabledFactory _factory;

    public TelegramLinkTests(TelegramEnabledFactory factory) => _factory = factory;

    private Task<T> WithServiceAsync<T>(Func<ITelegramLinkService, Task<T>> action) =>
        _factory.WithScopeAsync(services =>
            action(services.GetRequiredService<ITelegramLinkService>()));

    [Fact]
    public async Task A_valid_token_links_the_account()
    {
        var userId = await _factory.CreateMemberAsync("tg-link-ok");

        var token = await IssueTokenAsync(userId);
        var result = await WithServiceAsync(s => s.RedeemAsync(token, 555_000_001, "someone"));

        Assert.True(result.Succeeded);

        var user = await _factory.FindUserAsync(userId);
        Assert.Equal(555_000_001, user!.TelegramUserId);
        Assert.Equal("someone", user.TelegramUsername);
        Assert.NotNull(user.TelegramLinkedAt);
    }

    [Fact]
    public async Task A_token_works_only_once()
    {
        var userId = await _factory.CreateMemberAsync("tg-link-single-use");

        var token = await IssueTokenAsync(userId);

        Assert.True((await WithServiceAsync(s => s.RedeemAsync(token, 555_000_002, null))).Succeeded);

        // The same link, pressed again — or replayed by somebody who saw it in a chat.
        var second = await WithServiceAsync(s => s.RedeemAsync(token, 555_000_099, null));

        Assert.False(second.Succeeded);
        Assert.Equal(TelegramErrors.InvalidToken, second.ErrorKey);
    }

    [Fact]
    public async Task Issuing_a_new_token_retires_the_previous_one()
    {
        // A link generated, abandoned, and left sitting in a chat history must stop working
        // the moment a fresh one is requested.
        var userId = await _factory.CreateMemberAsync("tg-link-supersede");

        var first = await IssueTokenAsync(userId);
        var second = await IssueTokenAsync(userId);

        var stale = await WithServiceAsync(s => s.RedeemAsync(first, 555_000_003, null));
        Assert.False(stale.Succeeded);

        Assert.True((await WithServiceAsync(s => s.RedeemAsync(second, 555_000_003, null))).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    [InlineData("../../etc/passwd")]
    public async Task A_bogus_token_is_refused(string token)
    {
        var result = await WithServiceAsync(s => s.RedeemAsync(token, 555_000_004, null));

        Assert.False(result.Succeeded);
        Assert.Equal(TelegramErrors.InvalidToken, result.ErrorKey);
    }

    [Fact]
    public async Task An_unknown_and_an_expired_token_are_indistinguishable()
    {
        // Telling the two apart would confirm to a stranger that a token had once been real.
        var userId = await _factory.CreateMemberAsync("tg-link-indistinguishable");
        var token = await IssueTokenAsync(userId);

        await _factory.ExpireTokensAsync(userId);

        var expired = await WithServiceAsync(s => s.RedeemAsync(token, 555_000_005, null));
        var unknown = await WithServiceAsync(s => s.RedeemAsync("completely-made-up", 555_000_005, null));

        Assert.Equal(unknown.ErrorKey, expired.ErrorKey);
    }

    [Fact]
    public async Task One_telegram_account_cannot_serve_two_portal_accounts()
    {
        // Otherwise one chat would receive two members' notifications.
        var first = await _factory.CreateMemberAsync("tg-link-first");
        var second = await _factory.CreateMemberAsync("tg-link-second");

        Assert.True((await WithServiceAsync(s =>
            s.RedeemAsync(IssueTokenAsync(first).Result, 555_000_010, null))).Succeeded);

        var clash = await WithServiceAsync(s =>
            s.RedeemAsync(IssueTokenAsync(second).Result, 555_000_010, null));

        Assert.False(clash.Succeeded);
        Assert.Equal(TelegramErrors.AlreadyLinkedToAnotherAccount, clash.ErrorKey);

        var secondUser = await _factory.FindUserAsync(second);
        Assert.Null(secondUser!.TelegramUserId);
    }

    [Fact]
    public async Task The_raw_token_is_never_stored()
    {
        // Only a hash is kept: the token travels through Telegram's servers and lands in a chat
        // history, so it is treated as a credential in transit.
        var userId = await _factory.CreateMemberAsync("tg-link-hashed");
        var token = await IssueTokenAsync(userId);

        var stored = await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.TelegramLinkTokens
                .AsNoTracking()
                .Where(t => t.UserId == userId)
                .Select(t => t.TokenHash)
                .ToListAsync();
        });

        Assert.NotEmpty(stored);
        Assert.All(stored, hash => Assert.DoesNotContain(token, hash, StringComparison.Ordinal));
        Assert.All(stored, hash => Assert.Equal(64, hash.Length));
    }

    [Fact]
    public async Task Unlinking_clears_the_association()
    {
        var userId = await _factory.CreateMemberAsync("tg-unlink");
        await WithServiceAsync(s => s.RedeemAsync(IssueTokenAsync(userId).Result, 555_000_020, null));

        var result = await WithServiceAsync(s => s.UnlinkAsync(userId));
        Assert.True(result.Succeeded);

        var user = await _factory.FindUserAsync(userId);
        Assert.Null(user!.TelegramUserId);
        Assert.Null(user.TelegramLinkedAt);
    }

    [Fact]
    public async Task Linking_records_an_audit_entry_without_the_token()
    {
        var userId = await _factory.CreateMemberAsync("tg-link-audit");
        var token = await IssueTokenAsync(userId);

        await WithServiceAsync(s => s.RedeemAsync(token, 555_000_030, "auditor"));

        var entries = await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.AuditLogs
                .AsNoTracking()
                .Where(a => a.EntityId == userId.ToString() && a.Action == "telegram.linked")
                .Select(a => a.MetadataJson)
                .ToListAsync();
        });

        Assert.NotEmpty(entries);
        Assert.All(entries, metadata =>
            Assert.DoesNotContain(token, metadata ?? string.Empty, StringComparison.Ordinal));
    }

    private async Task<string> IssueTokenAsync(Guid userId)
    {
        var invitation = await WithServiceAsync(s => s.CreateInvitationAsync(userId));

        Assert.True(invitation.Succeeded);
        Assert.NotNull(invitation.Value);

        // The deep link is https://t.me/<bot>?start=<token>.
        var deepLink = invitation.Value!.DeepLink;
        return deepLink[(deepLink.IndexOf("start=", StringComparison.Ordinal) + "start=".Length)..];
    }
}

internal static class TelegramTestQueries
{
    public static Task ExpireTokensAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

            await db.TelegramLinkTokens
                .Where(t => t.UserId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.ExpiresAt, now.AddMinutes(-1)));
        });
}
