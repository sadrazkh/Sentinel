using System.Net;
using System.Text;
using System.Text.Json;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// A real HTTP server standing in for a 3x-ui panel.
/// <para>
/// A genuine socket rather than a stubbed <c>HttpMessageHandler</c>, because the behaviour under
/// test lives below the message layer: the client's connect callback, its timeout, and how it
/// classifies a connection that drops mid-response. A handler stub would replace exactly the code
/// that needs proving.
/// </para>
/// </summary>
internal sealed class FakePanel : IAsyncDisposable
{
    /// <summary>The token this panel accepts. Obviously synthetic; never a real credential.</summary>
    public const string ValidToken = "fake-panel-token-0123456789";

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _sync = new();

    private FakePanel(HttpListener listener)
    {
        _listener = listener;
        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    /// <summary>Handlers by path, so a test only describes the endpoints it cares about.</summary>
    public Dictionary<string, Func<RecordedRequest, PanelReply>> Handlers { get; } = [];

    /// <summary>Applied to every request before the handler; the way a test simulates a sick panel.</summary>
    public Func<RecordedRequest, PanelReply?>? Interceptor { get; set; }

    public string BaseUrl { get; private set; } = string.Empty;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToList();
            }
        }
    }

    public static FakePanel Start()
    {
        // A free port is found by trying: HttpListener has no "port 0" mode, and a fixed port
        // would make the suite fail when something else holds it.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var port = Random.Shared.Next(31_000, 46_000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();

                return new FakePanel(listener) { BaseUrl = $"http://127.0.0.1:{port}" };
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Could not find a free port for the fake panel.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;

        var body = string.Empty;

        if (request.HasEntityBody)
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        var recorded = new RecordedRequest(
            request.HttpMethod,
            request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty,
            request.Url?.Query ?? string.Empty,
            request.Headers["Authorization"],
            body);

        lock (_sync)
        {
            _requests.Add(recorded);
        }

        var reply = Interceptor?.Invoke(recorded)
                    ?? (Handlers.TryGetValue(recorded.Path, out var handler)
                        ? handler(recorded)
                        : PanelReply.Json(404, new { success = false, msg = "not found" }));

        try
        {
            if (reply.Delay > TimeSpan.Zero)
            {
                await Task.Delay(reply.Delay, cancellationToken);
            }

            if (reply.AbortConnection)
            {
                // Drops the socket without a response. This is what a client must classify as
                // "unknown", because a write may already have been applied.
                context.Response.Abort();
                return;
            }

            context.Response.StatusCode = reply.StatusCode;
            context.Response.ContentType = reply.ContentType;

            var payload = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.ContentLength64 = payload.Length;

            await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client hung up, or the test is tearing down. Either way there is nobody to
            // report to, and throwing here would surface as an unrelated test failure.
        }
    }

    /// <summary>Rejects anything without the expected Bearer token, exactly as a panel does.</summary>
    public void RequireBearerToken() =>
        Interceptor = request =>
            request.Authorization == $"Bearer {ValidToken}"
                ? null
                : PanelReply.Json(401, new { success = false, msg = "unauthorized" });

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        _listener.Stop();
        _listener.Close();

        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // Teardown.
        }

        _stopping.Dispose();
    }
}

internal sealed record RecordedRequest(
    string Method,
    string Path,
    string Query,
    string? Authorization,
    string Body)
{
    /// <summary>The request body as JSON, for asserting on what the client actually sent.</summary>
    public JsonElement Json() => JsonDocument.Parse(Body).RootElement;
}

internal sealed record PanelReply(
    int StatusCode,
    string Body,
    string ContentType = "application/json",
    TimeSpan Delay = default,
    bool AbortConnection = false)
{
    public static PanelReply Json(int statusCode, object payload) =>
        new(statusCode, JsonSerializer.Serialize(payload));

    /// <summary>The panel's success envelope.</summary>
    public static PanelReply Ok(object? obj = null) =>
        Json(200, new { success = true, msg = string.Empty, obj });

    /// <summary>The panel answering and refusing — a final outcome.</summary>
    public static PanelReply Refused(string message) =>
        Json(200, new { success = false, msg = message, obj = (object?)null });

    public static PanelReply Dropped() =>
        new(0, string.Empty, AbortConnection: true);

    public static PanelReply Slow(TimeSpan delay, object? obj = null) =>
        Ok(obj) with { Delay = delay };

    public static PanelReply Garbage() =>
        new(200, "this is not json at all");
}
