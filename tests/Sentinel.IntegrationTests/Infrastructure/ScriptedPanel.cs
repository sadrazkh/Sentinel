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
/// </summary>
public sealed class ScriptedPanel : IThreeXUiClient
{
    private readonly ConcurrentDictionary<string, PanelClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PanelClientTraffic> _traffic = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<string> _calls = [];

    /// <summary>Forces the next call of a given kind to answer a particular way.</summary>
    private readonly ConcurrentDictionary<string, Queue<PanelOutcome>> _scripted = new(StringComparer.Ordinal);

    private readonly Lock _sync = new();

    /// <summary>Every call made, in order, as "operation:argument". Lets a test assert on the flow.</summary>
    public IReadOnlyList<string> Calls => _calls.Reverse().ToList();

    /// <summary>Clients the fake panel currently holds, as the real one would.</summary>
    public IReadOnlyDictionary<string, PanelClient> Clients => _clients;

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

    /// <summary>Puts a client on the panel without the portal having asked, to simulate a lost write.</summary>
    public void PlantClient(string email, IReadOnlyList<int> inboundIds)
    {
        _clients[email] = new PanelClient(email, "planted-sub", true, 0, null, 0, inboundIds);
        _traffic[email] = new PanelClientTraffic(email, 0, 0, 0, true, null, null, inboundIds.FirstOrDefault());
    }

    public void SetTraffic(string email, long uploadBytes, long downloadBytes, long allowanceBytes)
    {
        _traffic[email] = new PanelClientTraffic(
            email, uploadBytes, downloadBytes, allowanceBytes, true, null, DateTimeOffset.UtcNow, 1);
    }

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
        _calls.Add(argument is null ? operation : $"{operation}:{argument}");

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
            _clients.TryGetValue(email, out var client)
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
            _traffic.TryGetValue(email, out var traffic)
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
            _clients.ContainsKey(email)
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

        _clients[request.Email] = client;
        _traffic[request.Email] = new PanelClientTraffic(
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

        if (!_clients.ContainsKey(request.Email))
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

        _clients[request.Email] = client;

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

        if (_traffic.TryGetValue(email, out var traffic))
        {
            _traffic[email] = traffic with { UploadBytes = 0, DownloadBytes = 0 };
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

        _clients.TryRemove(email, out _);

        if (!keepTraffic)
        {
            _traffic.TryRemove(email, out _);
        }

        return Task.FromResult(PanelResult<bool>.Success(true));
    }
}
