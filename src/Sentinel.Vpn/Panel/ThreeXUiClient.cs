using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Security;

namespace Sentinel.Vpn.Panel;

/// <summary>
/// Talks to a 3x-ui panel over its documented REST API.
/// <para>
/// Authentication is a static API token created in the panel under Settings → Security → API
/// Token and sent as <c>Authorization: Bearer</c>. No login round-trip and no session cookie to
/// keep alive, which is what makes a stateless client per call correct here.
/// </para>
/// <para>
/// Every path is a constant in this file. The portal never forwards a caller-supplied path to a
/// panel: doing so would make it a general-purpose remote control for the panel, which is a much
/// larger thing to secure than the dozen operations provisioning actually needs.
/// </para>
/// <para>
/// One <see cref="HttpClient"/> for the process, with connection pooling across panels. The
/// endpoint travels per call because the portal talks to many.
/// </para>
/// </summary>
public sealed class ThreeXUiClient : IThreeXUiClient, IDisposable
{
    // ---- the panel's API surface, as documented in its openapi.json ---------------------
    private const string StatusPath = "panel/api/server/status";
    private const string XrayVersionPath = "panel/api/server/getXrayVersion";
    private const string InboundsSlimPath = "panel/api/inbounds/list/slim";
    private const string ClientsAddPath = "panel/api/clients/add";
    private const string ClientsOnlinePath = "panel/api/clients/onlines";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // The panel sends fields the portal does not model, and adds more between versions.
        // Ignoring them is what lets a panel upgrade not break provisioning.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _client;
    private readonly ThreeXUiOptions _options;
    private readonly ILogger<ThreeXUiClient> _logger;

    public ThreeXUiClient(IOptions<ThreeXUiOptions> options, ILogger<ThreeXUiClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            // Redirects are not followed. A panel does not legitimately redirect its API, and
            // following one would carry the Bearer token to wherever it pointed.
            AllowAutoRedirect = false,

            // No cookie jar. The token is the whole credential, and a shared jar would let one
            // panel set a cookie that then travels to another.
            UseCookies = false,

            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,

            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, _options.TimeoutSeconds)),
            AutomaticDecompression = DecompressionMethods.All,

            // The same connect-time address validation every outbound client uses.
            ConnectCallback = ValidatedAddressConnector.Create(_options.AllowLoopbackPanelUrls),
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            MaxResponseContentBufferSize = _options.MaxResponseBytes,
        };
    }

    // ------------------------------------------------------------------------- reads ----

    public async Task<PanelResult<PanelStatus>> GetStatusAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        // GET, not POST. The panel registers this one with g.GET("/status"), and a POST to it is
        // routed as "no such path" — which surfaced as "check the base path" on a panel whose base
        // path was perfectly correct and whose inbound calls were already working.
        var status = await SendAsync<JsonElement>(
            endpoint, HttpMethod.Get, StatusPath, null, cancellationToken);

        if (!status.IsSuccess)
        {
            return PanelResult<PanelStatus>.Failure(status.Outcome, status.Message);
        }

        var version = await SendAsync<JsonElement>(
            endpoint, HttpMethod.Get, XrayVersionPath, null, cancellationToken);

        // Xray state lives under obj.xray.state in the status payload. Read defensively: the
        // health check must report "degraded", not throw, when the shape is not what we expect.
        var running = TryReadString(status.Value, "xray", "state") is { } state
                      && state.Equals("running", StringComparison.OrdinalIgnoreCase);

        return PanelResult<PanelStatus>.Success(new PanelStatus(
            running,
            version.IsSuccess ? AsString(version.Value) : null));
    }

    public async Task<PanelResult<IReadOnlyList<PanelInbound>>> ListInboundsAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        // The slim variant: the full one embeds every client on every inbound, which on a busy
        // panel is megabytes the portal has no use for.
        var result = await SendAsync<List<InboundDto>>(
            endpoint, HttpMethod.Get, InboundsSlimPath, null, cancellationToken);

        if (!result.IsSuccess)
        {
            return PanelResult<IReadOnlyList<PanelInbound>>.Failure(result.Outcome, result.Message);
        }

        var inbounds = (result.Value ?? [])
            .Select(dto => new PanelInbound(
                dto.Id, dto.Remark ?? string.Empty, dto.Protocol ?? string.Empty, dto.Enable, dto.Port))
            .ToList();

        return PanelResult<IReadOnlyList<PanelInbound>>.Success(inbounds);
    }

    public async Task<PanelResult<PanelClient>> GetClientAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (!PanelClientEmail.IsValid(email))
        {
            return PanelResult<PanelClient>.Failure(
                PanelOutcome.Blocked, "The client identifier is not one this portal generates.");
        }

        var result = await SendAsync<ClientDto>(
            endpoint, HttpMethod.Get, $"panel/api/clients/get/{Escape(email)}", null, cancellationToken);

        return result.IsSuccess && result.Value is { } dto
            ? PanelResult<PanelClient>.Success(dto.ToClient())
            : PanelResult<PanelClient>.Failure(result.Outcome, result.Message);
    }

    public async Task<PanelResult<PanelClientTraffic>> GetTrafficAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (!PanelClientEmail.IsValid(email))
        {
            return PanelResult<PanelClientTraffic>.Failure(
                PanelOutcome.Blocked, "The client identifier is not one this portal generates.");
        }

        var result = await SendAsync<TrafficDto>(
            endpoint, HttpMethod.Get, $"panel/api/clients/traffic/{Escape(email)}", null, cancellationToken);

        return result.IsSuccess && result.Value is { } dto
            ? PanelResult<PanelClientTraffic>.Success(dto.ToTraffic())
            : PanelResult<PanelClientTraffic>.Failure(result.Outcome, result.Message);
    }

    public async Task<PanelResult<IReadOnlyList<string>>> GetClientLinksAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (!PanelClientEmail.IsValid(email))
        {
            return PanelResult<IReadOnlyList<string>>.Failure(
                PanelOutcome.Blocked, "The client identifier is not one this portal generates.");
        }

        var result = await SendAsync<List<string>>(
            endpoint, HttpMethod.Get, $"panel/api/clients/links/{Escape(email)}", null, cancellationToken);

        return result.IsSuccess
            ? PanelResult<IReadOnlyList<string>>.Success(result.Value ?? [])
            : PanelResult<IReadOnlyList<string>>.Failure(result.Outcome, result.Message);
    }

    public async Task<PanelResult<IReadOnlyList<string>>> GetOnlineClientsAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<List<string>>(
            endpoint, HttpMethod.Post, ClientsOnlinePath, null, cancellationToken);

        return result.IsSuccess
            ? PanelResult<IReadOnlyList<string>>.Success(result.Value ?? [])
            : PanelResult<IReadOnlyList<string>>.Failure(result.Outcome, result.Message);
    }

    // ------------------------------------------------------------------------ writes ----

    public async Task<PanelResult<PanelClient>> CreateClientAsync(
        PanelEndpoint endpoint,
        PanelClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Reject(request) is { } rejection)
        {
            return PanelResult<PanelClient>.Failure(PanelOutcome.Blocked, rejection);
        }

        var body = new
        {
            client = ToClientPayload(request),
            inboundIds = request.InboundIds,
        };

        var result = await SendAsync<JsonElement>(
            endpoint, HttpMethod.Post, ClientsAddPath, body, cancellationToken);

        if (!result.IsSuccess)
        {
            return PanelResult<PanelClient>.Failure(result.Outcome, result.Message);
        }

        // The add response does not carry the whole client, so it is read back. That also confirms
        // the panel really applied it rather than answering optimistically.
        return await GetClientAsync(endpoint, request.Email, cancellationToken);
    }

    public async Task<PanelResult<PanelClient>> UpdateClientAsync(
        PanelEndpoint endpoint,
        PanelClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Reject(request) is { } rejection)
        {
            return PanelResult<PanelClient>.Failure(PanelOutcome.Blocked, rejection);
        }

        // The panel replaces the row rather than patching it, so the payload carries the full
        // field set. Anything omitted here would be wiped on the panel.
        var result = await SendAsync<JsonElement>(
            endpoint,
            HttpMethod.Post,
            $"panel/api/clients/update/{Escape(request.Email)}",
            ToClientPayload(request),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return PanelResult<PanelClient>.Failure(result.Outcome, result.Message);
        }

        return await GetClientAsync(endpoint, request.Email, cancellationToken);
    }

    public Task<PanelResult<bool>> AttachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken = default) =>
        AttachOrDetachAsync(endpoint, email, inboundIds, "attach", cancellationToken);

    public Task<PanelResult<bool>> DetachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken = default) =>
        AttachOrDetachAsync(endpoint, email, inboundIds, "detach", cancellationToken);

    public async Task<PanelResult<bool>> ResetTrafficAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (!PanelClientEmail.IsValid(email))
        {
            return PanelResult<bool>.Failure(
                PanelOutcome.Blocked, "The client identifier is not one this portal generates.");
        }

        var result = await SendAsync<JsonElement>(
            endpoint,
            HttpMethod.Post,
            $"panel/api/clients/resetTraffic/{Escape(email)}",
            null,
            cancellationToken);

        return result.IsSuccess
            ? PanelResult<bool>.Success(true)
            : PanelResult<bool>.Failure(result.Outcome, result.Message);
    }

    public async Task<PanelResult<bool>> DeleteClientAsync(
        PanelEndpoint endpoint,
        string email,
        bool keepTraffic,
        CancellationToken cancellationToken = default)
    {
        if (!PanelClientEmail.IsValid(email))
        {
            return PanelResult<bool>.Failure(
                PanelOutcome.Blocked, "The client identifier is not one this portal generates.");
        }

        // keepTraffic preserves the usage record, which is what a migration needs: the counters
        // are the customer's remaining allowance, not disposable telemetry.
        var path = $"panel/api/clients/del/{Escape(email)}" + (keepTraffic ? "?keepTraffic=1" : string.Empty);

        var result = await SendAsync<JsonElement>(
            endpoint, HttpMethod.Post, path, null, cancellationToken);

        // A client that is already gone is the state the caller wanted.
        if (result.Outcome == PanelOutcome.NotFound)
        {
            return PanelResult<bool>.Success(true);
        }

        return result.IsSuccess
            ? PanelResult<bool>.Success(true)
            : PanelResult<bool>.Failure(result.Outcome, result.Message);
    }

    // ------------------------------------------------------------------------ plumbing ----

    private async Task<PanelResult<bool>> AttachOrDetachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!PanelClientEmail.IsValid(email))
        {
            return PanelResult<bool>.Failure(
                PanelOutcome.Blocked, "The client identifier is not one this portal generates.");
        }

        if (inboundIds is null || inboundIds.Count == 0 || inboundIds.Any(id => id <= 0))
        {
            return PanelResult<bool>.Failure(
                PanelOutcome.Blocked, "At least one positive inbound id is required.");
        }

        var result = await SendAsync<JsonElement>(
            endpoint,
            HttpMethod.Post,
            $"panel/api/clients/{Escape(email)}/{operation}",
            new { inboundIds },
            cancellationToken);

        return result.IsSuccess
            ? PanelResult<bool>.Success(true)
            : PanelResult<bool>.Failure(result.Outcome, result.Message);
    }

    /// <summary>
    /// The client payload the panel expects.
    /// <para>
    /// No <c>id</c>, no <c>password</c>, no <c>auth</c>, no <c>flow</c>, no <c>subId</c>: the panel
    /// generates every per-protocol secret when they are omitted. Leaving that to the panel means
    /// the portal never holds a customer's credential and can never be the source of a weak one.
    /// </para>
    /// <para>
    /// <c>totalGB</c> is the panel's field name but its unit is bytes, and <c>expiryTime</c> is
    /// epoch milliseconds. Both conversions live here so nothing else has to remember.
    /// </para>
    /// </summary>
    private static object ToClientPayload(PanelClientRequest request) => new
    {
        email = request.Email,
        totalGB = request.TotalAllowanceBytes,
        expiryTime = request.ExpiresAt?.ToUnixTimeMilliseconds() ?? 0L,
        limitIp = request.IpLimit,
        enable = request.Enabled,
        tgId = 0,
    };

    /// <summary>
    /// Refuses a request the portal should never have built. These are invariants, not user input
    /// validation — a negative allowance or an unnumbered inbound means a bug upstream, and sending
    /// it to the panel would turn that bug into a customer-visible misconfiguration.
    /// </summary>
    private static string? Reject(PanelClientRequest request)
    {
        if (!PanelClientEmail.IsValid(request.Email))
        {
            return "The client identifier is not one this portal generates.";
        }

        if (request.InboundIds.Count == 0 || request.InboundIds.Any(id => id <= 0))
        {
            return "At least one positive inbound id is required.";
        }

        if (request.TotalAllowanceBytes < 0)
        {
            return "A traffic allowance cannot be negative.";
        }

        if (request.IpLimit < 0)
        {
            return "An IP limit cannot be negative.";
        }

        return null;
    }

    private async Task<PanelResult<T>> SendAsync<T>(
        PanelEndpoint endpoint,
        HttpMethod method,
        string apiPath,
        object? body,
        CancellationToken cancellationToken)
    {
        if (PanelBaseUrlPolicy.Validate(
                endpoint.BaseUrl, _options.AllowInsecurePanelUrls, out var baseUri)
            != PanelUrlRejection.None)
        {
            // Nothing was sent, so this outcome is certain and safe — never UnknownOutcome.
            return PanelResult<T>.Failure(PanelOutcome.Blocked, "The panel address is not allowed.");
        }

        using var request = new HttpRequestMessage(method, PanelBaseUrlPolicy.Combine(baseUri!, apiPath));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiToken);
        request.Headers.Accept.ParseAdd("application/json");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        try
        {
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Worth surfacing loudly: the stored credential is wrong or was revoked. The
                // token itself is never logged.
                _logger.LogError(
                    "Panel at {Host} refused the API token ({StatusCode}).",
                    baseUri!.Host,
                    (int)response.StatusCode);

                return PanelResult<T>.Failure(PanelOutcome.Unauthorized, "The panel refused the API token.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return PanelResult<T>.Failure(PanelOutcome.NotFound);
            }

            if ((int)response.StatusCode >= 500)
            {
                // A 5xx on a write may or may not have been applied before the panel failed.
                return PanelResult<T>.Failure(
                    PanelOutcome.UnknownOutcome,
                    $"The panel returned {(int)response.StatusCode}.");
            }

            var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, cancellationToken);

            if (envelope is null)
            {
                return PanelResult<T>.Failure(
                    PanelOutcome.UnknownOutcome, "The panel returned an empty response.");
            }

            if (!envelope.Success)
            {
                // The panel answered and said no. Final: a retry would get the same answer.
                return PanelResult<T>.Failure(PanelOutcome.Rejected, Redact(envelope.Msg));
            }

            return PanelResult<T>.Success(envelope.Obj!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. Not our outcome to classify, and not safe to call unapplied.
            throw;
        }
        catch (Exception ex) when (FindBlocked(ex) is { } blocked)
        {
            // The connect callback refused the address, so nothing left the process. This is
            // checked before the general network case below because the exception arrives wrapped
            // in an HttpRequestException — matching on the outer type alone would classify a
            // blocked address as "unknown" and send a caller into reconciliation over a
            // misconfiguration that is actually certain and safe.
            _logger.LogWarning("Refused to reach panel {Host}: {Reason}", baseUri!.Host, blocked.Message);

            return PanelResult<T>.Failure(PanelOutcome.Blocked, "The panel address is not allowed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A timeout, a reset connection or an unparseable body: on a write, the panel may
            // still have applied it. This is the one outcome that must never be blind-retried.
            _logger.LogWarning(
                ex, "Panel call to {Host}{Path} ended without a usable answer.", baseUri!.Host, apiPath);

            return PanelResult<T>.Failure(PanelOutcome.UnknownOutcome, "The panel did not answer usably.");
        }
    }

    /// <summary>
    /// Walks the whole inner-exception chain looking for a refused address.
    /// <para>
    /// The chain, not just <c>InnerException</c>: the connect callback's exception can be nested
    /// more than one level deep depending on where in the handshake it surfaces, and a check that
    /// only looked one level down would work today and quietly stop working later.
    /// </para>
    /// </summary>
    private static BlockedAddressException? FindBlocked(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BlockedAddressException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps a panel's own message short and free of anything that might carry a secret. Panel
    /// messages end up in audit metadata and logs.
    /// </summary>
    private static string? Redact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();

        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string? AsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string? TryReadString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    public void Dispose() => _client.Dispose();

    // ---- wire shapes -------------------------------------------------------------------

    /// <summary>Every panel response is wrapped in this. <c>obj</c> carries the payload.</summary>
    private sealed record Envelope<T>(bool Success, string? Msg, T? Obj);

    private sealed record InboundDto(int Id, string? Remark, string? Protocol, bool Enable, int Port);

    private sealed record ClientDto(
        string? Email,
        string? SubId,
        bool Enable,
        long TotalGB,
        long ExpiryTime,
        int LimitIp,
        List<int>? InboundIds)
    {
        public PanelClient ToClient() => new(
            Email ?? string.Empty,
            SubId,
            Enable,
            TotalGB,
            FromEpochMilliseconds(ExpiryTime),
            LimitIp,
            InboundIds ?? []);
    }

    private sealed record TrafficDto(
        string? Email,
        long Up,
        long Down,
        long Total,
        bool Enable,
        long ExpiryTime,
        long LastOnline,
        int InboundId)
    {
        public PanelClientTraffic ToTraffic() => new(
            Email ?? string.Empty,
            Up,
            Down,
            Total,
            Enable,
            FromEpochMilliseconds(ExpiryTime),
            FromEpochMilliseconds(LastOnline),
            InboundId);
    }

    /// <summary>
    /// The panel uses epoch milliseconds, and zero for "not set". A negative value has been seen
    /// in the wild for a client whose expiry was expressed as a countdown, so it is treated as
    /// unset too rather than becoming a date in 1969.
    /// </summary>
    private static DateTimeOffset? FromEpochMilliseconds(long value) =>
        value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
}
