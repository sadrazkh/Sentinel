using System.Collections.Concurrent;
using Sentinel.Vpn.Panel;

namespace Sentinel.IntegrationTests.Infrastructure;

/// <summary>
/// A panel whose answers a test dictates.
/// <para>
/// Deliberately not a real HTTP fake. The unit suite already drives the real
/// <see cref="ThreeXUiClient"/> against a live socket to prove it classifies a dropped connection, a
/// timeout and a refusal correctly. What the provisioning saga needs proving about is the other half:
/// that it responds correctly to each classification — and forcing an
/// <see cref="PanelOutcome.UnknownOutcome"/> on the third call but not the first is far easier to
/// express here than by orchestrating a socket.
/// </para>
/// <para>
/// State is kept <b>per endpoint</b>, not per e-mail. One instance therefore stands in for every
/// panel the portal talks to, which is what migration needs: "the client is on the source and not yet
/// on the destination" is the state the whole saga turns on, and a store keyed by e-mail alone cannot
/// express it.
/// </para>
/// </summary>
public sealed class ScriptedPanel : IThreeXUiClient
{
    /// <summary>Keyed by "baseUrl\nemail", so each panel has its own set of clients.</summary>
    private readonly ConcurrentDictionary<string, PanelClient> _clients = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PanelClientTraffic> _traffic = new(StringComparer.Ordinal);

    /// <summary>Traffic records kept by a delete with <c>keepTraffic: true</c>.</summary>
    private readonly ConcurrentDictionary<string, PanelClientTraffic> _retainedTraffic =
        new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Forces the next call of a given kind to answer a particular way.</summary>
    private readonly ConcurrentDictionary<string, Queue<PanelOutcome>> _scripted = new(StringComparer.Ordinal);

    private readonly Lock _sync = new();

    /// <summary>Every call made, in order, as "operation:argument". Lets a test assert on the flow.</summary>
    public IReadOnlyList<string> Calls => _calls.ToList();

    /// <summary>When true, every call answers <see cref="PanelOutcome.UnknownOutcome"/>.</summary>
    public bool AllCallsUnknown { get; set; }

    /// <summary>
    /// Queues one forced outcome for an operation. Applied once, then the panel behaves normally
    /// again — which is how a real transient failure behaves.
    /// </summary>
    public void ScriptOnce(string operation, PanelOutcome outcome)
    {
        lock (_sync)
        {
            var queue = _scripted.GetOrAdd(operation, _ => new Queue<PanelOutcome>());
            queue.Enqueue(outcome);
        }
    }

    /// <summary>
    /// Drops any outcome still queued. A test that scripts an outcome its own sweep never reaches
    /// would otherwise hand that outcome to the next test, which is a very confusing failure.
    /// </summary>
    public void ClearScripts()
    {
        lock (_sync)
        {
            _scripted.Clear();
        }
    }

    // -------------------------------------------------------------------- test observation ----

    /// <summary>Every client on every panel, keyed by e-mail. For assertions that ignore placement.</summary>
    public IReadOnlyDictionary<string, PanelClient> Clients =>
        _clients.ToDictionary(entry => EmailOf(entry.Key), entry => entry.Value, StringComparer.Ordinal);

    /// <summary>Whether a given panel holds this client. The question migration is really about.</summary>
    public bool Has(string baseUrl, string email) => _clients.ContainsKey(Key(baseUrl, email));

    public PanelClient? ClientOn(string baseUrl, string email) =>
        _clients.TryGetValue(Key(baseUrl, email), out var client) ? client : null;

    /// <summary>How many panels hold a client with this e-mail. Two means dual-active.</summary>
    public int PanelCountFor(string email) =>
        _clients.Keys.Count(key => EmailOf(key).Equals(email, StringComparison.Ordinal));

    /// <summary>Whether a delete left the traffic record behind, which is what keepTraffic means.</summary>
    public bool HasRetainedTraffic(string baseUrl, string email) =>
        _retainedTraffic.ContainsKey(Key(baseUrl, email));

    /// <summary>Puts a client on one panel without the portal having asked, to simulate a lost write.</summary>
    public void PlantClient(string baseUrl, string email, IReadOnlyList<int> inboundIds)
    {
        var key = Key(baseUrl, email);

        _clients[key] = new PanelClient(email, "planted-sub", true, 0, null, 0, inboundIds);
        _traffic[key] = new PanelClientTraffic(
            email, 0, 0, 0, true, null, null, inboundIds.FirstOrDefault());
    }

    /// <summary>Removes a client from one panel behind the portal's back.</summary>
    public void RemoveClient(string baseUrl, string email)
    {
        var key = Key(baseUrl, email);

        _clients.TryRemove(key, out _);
        _traffic.TryRemove(key, out _);
    }

    public void SetTraffic(string baseUrl, string email, long uploadBytes, long downloadBytes, long allowanceBytes)
    {
        _traffic[Key(baseUrl, email)] = new PanelClientTraffic(
            email, uploadBytes, downloadBytes, allowanceBytes, true, null, DateTimeOffset.UtcNow, 1);
    }

    // ---------------------------------------------------------------------------- plumbing ----

    private static string Key(string baseUrl, string email) => $"{baseUrl}\n{email}";

    private static string EmailOf(string key) => key[(key.IndexOf('\n') + 1)..];

    private PanelOutcome? NextScripted(string operation)
    {
        if (AllCallsUnknown)
        {
            return PanelOutcome.UnknownOutcome;
        }

        lock (_sync)
        {
            if (_scripted.TryGetValue(operation, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }
        }

        return null;
    }

    private PanelResult<T>? Intercept<T>(string operation, string? argument = null)
    {
        _calls.Enqueue(argument is null ? operation : $"{operation}:{argument}");

        return NextScripted(operation) is { } forced
            ? PanelResult<T>.Failure(forced, $"scripted {forced}")
            : null;
    }

    // ------------------------------------------------------------------------------ reads ----

    public Task<PanelResult<PanelStatus>> GetStatusAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Intercept<PanelStatus>("status")
            ?? PanelResult<PanelStatus>.Success(new PanelStatus(true, "25.1.1")));

    public Task<PanelResult<IReadOnlyList<PanelInbound>>> ListInboundsAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Intercept<IReadOnlyList<PanelInbound>>("inbounds")
            ?? PanelResult<IReadOnlyList<PanelInbound>>.Success(
                [new PanelInbound(1, "VLESS-443", "vless", true, 443)]));

    public Task<PanelResult<PanelClient>> GetClientAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<PanelClient>("get", email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        return Task.FromResult(
            _clients.TryGetValue(Key(endpoint.BaseUrl, email), out var client)
                ? PanelResult<PanelClient>.Success(client)
                : PanelResult<PanelClient>.Failure(PanelOutcome.NotFound));
    }

    public Task<PanelResult<PanelClientTraffic>> GetTrafficAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<PanelClientTraffic>("traffic", email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        return Task.FromResult(
            _traffic.TryGetValue(Key(endpoint.BaseUrl, email), out var traffic)
                ? PanelResult<PanelClientTraffic>.Success(traffic)
                : PanelResult<PanelClientTraffic>.Failure(PanelOutcome.NotFound));
    }

    public Task<PanelResult<IReadOnlyList<string>>> GetClientLinksAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<IReadOnlyList<string>>("links", email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        return Task.FromResult(
            _clients.ContainsKey(Key(endpoint.BaseUrl, email))
                ? PanelResult<IReadOnlyList<string>>.Success(
                    [$"vless://uuid-for-{email}@host.example.com:443?type=tcp#{email}"])
                : PanelResult<IReadOnlyList<string>>.Failure(PanelOutcome.NotFound));
    }

    public Task<PanelResult<IReadOnlyList<string>>> GetOnlineClientsAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Intercept<IReadOnlyList<string>>("onlines")
            ?? PanelResult<IReadOnlyList<string>>.Success([]));

    // ----------------------------------------------------------------------------- writes ----

    public Task<PanelResult<PanelClient>> CreateClientAsync(
        PanelEndpoint endpoint,
        PanelClientRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<PanelClient>("create", request.Email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        var client = new PanelClient(
            request.Email,
            $"sub-{request.Email}",
            request.Enabled,
            request.TotalAllowanceBytes,
            request.ExpiresAt,
            request.IpLimit,
            request.InboundIds);

        var key = Key(endpoint.BaseUrl, request.Email);

        _clients[key] = client;
        _traffic[key] = new PanelClientTraffic(
            request.Email, 0, 0, request.TotalAllowanceBytes, request.Enabled,
            request.ExpiresAt, null, request.InboundIds.FirstOrDefault());

        return Task.FromResult(PanelResult<PanelClient>.Success(client));
    }

    public Task<PanelResult<PanelClient>> UpdateClientAsync(
        PanelEndpoint endpoint,
        PanelClientRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<PanelClient>("update", request.Email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        var key = Key(endpoint.BaseUrl, request.Email);

        if (!_clients.ContainsKey(key))
        {
            return Task.FromResult(PanelResult<PanelClient>.Failure(PanelOutcome.NotFound));
        }

        var client = new PanelClient(
            request.Email,
            $"sub-{request.Email}",
            request.Enabled,
            request.TotalAllowanceBytes,
            request.ExpiresAt,
            request.IpLimit,
            request.InboundIds);

        _clients[key] = client;

        return Task.FromResult(PanelResult<PanelClient>.Success(client));
    }

    public Task<PanelResult<bool>> AttachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Intercept<bool>("attach", email) ?? PanelResult<bool>.Success(true));

    public Task<PanelResult<bool>> DetachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Intercept<bool>("detach", email) ?? PanelResult<bool>.Success(true));

    public Task<PanelResult<bool>> ResetTrafficAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<bool>("reset", email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        var key = Key(endpoint.BaseUrl, email);

        if (_traffic.TryGetValue(key, out var traffic))
        {
            _traffic[key] = traffic with { UploadBytes = 0, DownloadBytes = 0 };
        }

        return Task.FromResult(PanelResult<bool>.Success(true));
    }

    public Task<PanelResult<bool>> DeleteClientAsync(
        PanelEndpoint endpoint,
        string email,
        bool keepTraffic,
        CancellationToken cancellationToken = default)
    {
        if (Intercept<bool>("delete", email) is { } forced)
        {
            return Task.FromResult(forced);
        }

        var key = Key(endpoint.BaseUrl, email);

        _clients.TryRemove(key, out _);

        if (_traffic.TryRemove(key, out var traffic) && keepTraffic)
        {
            // The panel keeps the usage row when asked to. Migration relies on this: it is the record
            // of what the customer used before the move.
            _retainedTraffic[key] = traffic;
        }

        return Task.FromResult(PanelResult<bool>.Success(true));
    }
}
