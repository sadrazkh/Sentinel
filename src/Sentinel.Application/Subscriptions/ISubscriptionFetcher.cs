namespace Sentinel.Application.Subscriptions;

public enum SubscriptionFetchOutcome
{
    Succeeded = 0,
    /// <summary>The URL itself was rejected before any connection was attempted.</summary>
    RejectedUrl = 1,
    /// <summary>The target resolved to an address the connection policy refuses.</summary>
    BlockedAddress = 2,
    Timeout = 3,
    /// <summary>Reached the server, but it answered with an error status.</summary>
    UpstreamError = 4,
    /// <summary>The response was larger than the cap, or was not something we can read.</summary>
    UnusableResponse = 5,
    NetworkError = 6,
}

/// <summary>
/// The result of one fetch. <see cref="Reason"/> is a short operator-facing string; the
/// response body never appears in it, because it belongs to a third-party server and would end
/// up in logs and on an admin page.
/// </summary>
public sealed record SubscriptionFetchResult(
    SubscriptionFetchOutcome Outcome,
    SubscriptionContent Content,
    string? Reason)
{
    public bool Succeeded => Outcome == SubscriptionFetchOutcome.Succeeded;

    public static SubscriptionFetchResult Success(SubscriptionContent content) =>
        new(SubscriptionFetchOutcome.Succeeded, content, null);

    public static SubscriptionFetchResult Failure(SubscriptionFetchOutcome outcome, string reason) =>
        new(outcome, SubscriptionContent.Empty, reason);
}

public interface ISubscriptionFetcher
{
    /// <summary>
    /// Retrieves and parses a subscription. Never throws for an unreachable or hostile target —
    /// a failed fetch is an ordinary outcome that the caller records against the source.
    /// </summary>
    Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}
