using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Subscriptions;

namespace Sentinel.Infrastructure.Subscriptions;

/// <summary>
/// Raised by the connect callback when a target resolves to an address we refuse to reach.
/// A distinct type so the caller can report "blocked" rather than a generic network failure.
/// </summary>
public sealed class BlockedAddressException : Exception
{
    public BlockedAddressException(string message) : base(message)
    {
    }
}

/// <summary>
/// Retrieves a subscription over HTTP with the SSRF defences applied.
/// <para>
/// Three layers, and the middle one is the one that matters most:
/// </para>
/// <list type="number">
/// <item>The URL is screened before anything is attempted (scheme, credentials, port, obvious
/// internal hosts) — see <see cref="SubscriptionUrlPolicy"/>.</item>
/// <item>Every TCP connection resolves the host, validates the resulting addresses, and then
/// connects <em>to a validated address</em>. Checking the hostname and letting the stack resolve
/// it again would leave a window in which DNS can answer differently the second time — the
/// rebinding attack. Connecting to the address that was actually checked closes it.</item>
/// <item>Redirects are followed manually so each hop goes through the same screening, and the
/// body is read through a hard byte cap.</item>
/// </list>
/// </summary>
public sealed class SubscriptionFetcher : ISubscriptionFetcher, IDisposable
{
    private readonly HttpClient _client;
    private readonly SubscriptionFetchOptions _options;
    private readonly ILogger<SubscriptionFetcher> _logger;

    public SubscriptionFetcher(
        IOptions<SubscriptionFetchOptions> options,
        ILogger<SubscriptionFetcher> logger)
    {
        _options = options.Value;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            // Redirects are followed by hand so every hop is re-screened.
            AllowAutoRedirect = false,

            // No cookie jar: a subscription fetch is stateless, and a shared container would
            // let one upstream set a cookie that is then sent to another.
            UseCookies = false,

            // No proxy and no default credentials — neither should ever be applied to a
            // request whose destination a member chose.
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,

            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, _options.TimeoutSeconds)),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectToValidatedAddressAsync,
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),

            // Belt and braces alongside the manual read cap below.
            MaxResponseContentBufferSize = _options.MaxResponseBytes,
        };

        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/plain, */*");
    }

    /// <summary>
    /// Resolves the host, refuses any address the policy rejects, and connects to one that
    /// passed. TLS still uses the original host name for SNI and certificate validation, so
    /// pinning the address costs nothing in correctness.
    /// </summary>
    private static async ValueTask<Stream> ConnectToValidatedAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] resolved;

        if (IPAddress.TryParse(host, out var literal))
        {
            resolved = [literal];
        }
        else
        {
            resolved = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }

        // Every returned address must pass. Connecting to the one good address in a set that
        // also contains an internal one would still be a successful rebinding attack, because
        // the attacker controls which address the resolver returns next.
        var rejected = resolved
            .Select(address => (Address: address, Rejection: IpAddressPolicy.Evaluate(address)))
            .FirstOrDefault(entry => entry.Rejection != IpRejection.None);

        if (rejected.Address is not null)
        {
            throw new BlockedAddressException(
                $"The target resolves to a disallowed address ({rejected.Rejection}).");
        }

        if (resolved.Length == 0)
        {
            throw new BlockedAddressException("The target did not resolve to any address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // Connect to the validated addresses, never to the host name again.
            await socket.ConnectAsync(resolved, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<SubscriptionFetchResult> FetchAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return SubscriptionFetchResult.Failure(
                SubscriptionFetchOutcome.RejectedUrl, "Subscription fetching is disabled.");
        }

        var rejection = SubscriptionUrlPolicy.Validate(url, out var uri);

        if (rejection != SubscriptionUrlRejection.None)
        {
            return SubscriptionFetchResult.Failure(
                SubscriptionFetchOutcome.RejectedUrl, rejection.ToString());
        }

        try
        {
            return await FetchFollowingRedirectsAsync(uri!, cancellationToken);
        }
        catch (BlockedAddressException ex)
        {
            _logger.LogWarning("Refused a subscription fetch: {Reason}", ex.Message);
            return SubscriptionFetchResult.Failure(SubscriptionFetchOutcome.BlockedAddress, ex.Message);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException blocked)
        {
            // The connect callback's exception arrives wrapped once the handler is involved.
            _logger.LogWarning("Refused a subscription fetch: {Reason}", blocked.Message);
            return SubscriptionFetchResult.Failure(SubscriptionFetchOutcome.BlockedAddress, blocked.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SubscriptionFetchResult.Failure(SubscriptionFetchOutcome.Timeout, "The request timed out.");
        }
        catch (HttpRequestException ex)
        {
            // The message is kept short and free of any response content.
            _logger.LogInformation("A subscription fetch failed: {Message}", ex.Message);
            return SubscriptionFetchResult.Failure(
                SubscriptionFetchOutcome.NetworkError, "The subscription host could not be reached.");
        }
    }

    private async Task<SubscriptionFetchResult> FetchFollowingRedirectsAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var current = uri;

        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;

                if (location is null)
                {
                    return SubscriptionFetchResult.Failure(
                        SubscriptionFetchOutcome.UpstreamError, "A redirect carried no destination.");
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);

                // Each hop is screened exactly like the original. A redirect to
                // http://169.254.169.254 is the classic way past a check that only looked at
                // the first URL.
                if (SubscriptionUrlPolicy.Validate(next.ToString(), out var validated)
                    != SubscriptionUrlRejection.None)
                {
                    return SubscriptionFetchResult.Failure(
                        SubscriptionFetchOutcome.BlockedAddress,
                        "A redirect pointed somewhere we will not follow.");
                }

                current = validated!;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return SubscriptionFetchResult.Failure(
                    SubscriptionFetchOutcome.UpstreamError,
                    $"The subscription host answered {(int)response.StatusCode}.");
            }

            return await ReadContentAsync(response, cancellationToken);
        }

        return SubscriptionFetchResult.Failure(
            SubscriptionFetchOutcome.UpstreamError, "Too many redirects.");
    }

    private async Task<SubscriptionFetchResult> ReadContentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // A declared length over the cap is refused before a single byte is read.
        if (response.Content.Headers.ContentLength is { } declared
            && declared > _options.MaxResponseBytes)
        {
            return SubscriptionFetchResult.Failure(
                SubscriptionFetchOutcome.UnusableResponse, "The response is larger than the limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                break;
            }

            // The real cap: a server that lies about Content-Length, or sends none at all,
            // runs into this instead of into our memory.
            if (accumulated.Length + read > _options.MaxResponseBytes)
            {
                return SubscriptionFetchResult.Failure(
                    SubscriptionFetchOutcome.UnusableResponse, "The response is larger than the limit.");
            }

            accumulated.Write(buffer, 0, read);
        }

        var body = Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);

        var userInfo = SubscriptionUserInfoParser.Parse(
            ReadHeader(response, SubscriptionUserInfoParser.HeaderName));

        var configs = SubscriptionParser.ParseBody(body);

        return SubscriptionFetchResult.Success(
            new SubscriptionContent(configs, userInfo, ReadHeader(response, "profile-title")));
    }

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : response.Content.Headers.TryGetValues(name, out var contentValues)
                ? contentValues.FirstOrDefault()
                : null;

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    public void Dispose() => _client.Dispose();
}
