using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Messaging;

namespace flow_weaver_backend.BackgroundServices;

// Slack Socket Mode connector. For each enabled Slack channel that has an
// App-Level Token configured, the backend dials OUT to Slack over a WebSocket
// and receives events on it — no public webhook / ingress required (ideal for
// VPN-only deployments). Events are fed into the same ingest pipeline as the
// HTTP path via ReceiveVerifiedAsync (the socket is already authenticated, so
// signature verification is skipped).
//
// A reconcile loop keeps one connection per channel in sync with the DB
// (start/stop/rotate). Each connection self-heals: on disconnect or error it
// reopens with backoff until cancelled. Multiple replicas opening connections is
// safe — Slack load-balances events across them (HA), and the inbound dedupe
// covers any overlap.
public sealed class SlackSocketModeHostedService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private const string OpenUrl = "https://slack.com/api/apps.connections.open";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SlackSocketModeHostedService> _logger;

    private readonly Dictionary<Guid, Connection> _connections = new();

    public SlackSocketModeHostedService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<SlackSocketModeHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private sealed record Connection(string Token, CancellationTokenSource Cts, Task Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SlackSocketMode started — reconcile every {Seconds}s", (int)ReconcileInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "slack.socket.reconcile_failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); } catch { break; }
        }

        foreach (var conn in _connections.Values) conn.Cts.Cancel();
        _connections.Clear();
        _logger.LogInformation("SlackSocketMode shutting down");
    }

    // Bring the set of live connections in line with the DB: start new ones, stop
    // removed/disabled ones, and restart on token rotation.
    private async Task ReconcileAsync(CancellationToken ct)
    {
        Dictionary<Guid, string> desired = new();
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<IMessagingChannelRepository>();
            var crypto = scope.ServiceProvider.GetRequiredService<ICredentialEncryptionService>();
            var list = await channels.ListEnabledByProviderAllTenantsAsync(MessagingChannel.ProviderSlack, ct);
            foreach (var c in list)
            {
                if (c.EncryptedAppToken is not { Length: > 0 }) continue;
                var token = crypto.Decrypt(c.EncryptedAppToken);
                if (!string.IsNullOrEmpty(token)) desired[c.MessagingChannelId] = token;
            }
        }

        // Stop connections that vanished, got disabled, or whose token rotated.
        foreach (var (id, conn) in _connections.ToList())
        {
            if (!desired.TryGetValue(id, out var token) || !string.Equals(token, conn.Token, StringComparison.Ordinal))
            {
                conn.Cts.Cancel();
                _connections.Remove(id);
                _logger.LogInformation("slack.socket.stop channel={Channel}", id);
            }
        }

        // Start connections that are newly desired.
        foreach (var (id, token) in desired)
        {
            if (_connections.ContainsKey(id)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = RunConnectionAsync(id, token, cts.Token);
            _connections[id] = new Connection(token, cts, task);
            _logger.LogInformation("slack.socket.start channel={Channel}", id);
        }
    }

    // One channel's connection: open → receive until close/disconnect → reopen
    // with capped exponential backoff, until cancelled.
    private async Task RunConnectionAsync(Guid channelId, string appToken, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wssUrl = await OpenConnectionAsync(appToken, ct);
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wssUrl), ct);
                attempt = 0;
                _logger.LogInformation("slack.socket.connected channel={Channel}", channelId);
                await ReceiveLoopAsync(channelId, ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "slack.socket.error channel={Channel} attempt={Attempt}", channelId, attempt);
            }

            if (ct.IsCancellationRequested) break;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            attempt++;
            try { await Task.Delay(delay, ct); } catch { break; }
        }

        _logger.LogInformation("slack.socket.closed channel={Channel}", channelId);
    }

    private async Task<string> OpenConnectionAsync(string appToken, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("messaging");
        using var req = new HttpRequestMessage(HttpMethod.Post, OpenUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"apps.connections.open failed: {Truncate(body, 200)}");
        return root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()!
            : throw new InvalidOperationException("apps.connections.open returned no url");
    }

    private async Task ReceiveLoopAsync(Guid channelId, ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (await HandleMessageAsync(channelId, ws, text, ct))
                return; // disconnect envelope → reopen
        }
    }

    // Returns true if the caller should reconnect (Slack asked us to disconnect).
    private async Task<bool> HandleMessageAsync(Guid channelId, ClientWebSocket ws, string text, CancellationToken ct)
    {
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(text); }
        catch (JsonException) { return false; }
        if (root.ValueKind != JsonValueKind.Object) return false;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "hello":
                return false;
            case "disconnect":
                _logger.LogInformation(
                    "slack.socket.disconnect channel={Channel} reason={Reason}",
                    channelId, root.TryGetProperty("reason", out var r) ? r.GetString() : "?");
                return true;
            case "events_api":
                await DispatchEventAsync(channelId, ws, root, ct);
                return false;
            default:
                return false; // slash_commands / interactive — not handled
        }
    }

    private async Task DispatchEventAsync(Guid channelId, ClientWebSocket ws, JsonElement envelope, CancellationToken ct)
    {
        var envelopeId = envelope.TryGetProperty("envelope_id", out var e) ? e.GetString() : null;

        if (envelope.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.GetRawText());
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var ingest = scope.ServiceProvider.GetRequiredService<IMessagingIngestService>();
                await ingest.ReceiveVerifiedAsync(MessagingChannel.ProviderSlack, channelId, bytes, ct);
            }
            catch (Exception ex)
            {
                // Don't ack on failure — Slack re-delivers, and the inbound dedupe
                // makes the retry a no-op once it succeeds.
                _logger.LogWarning(ex, "slack.socket.dispatch_failed channel={Channel}", channelId);
                return;
            }
        }

        if (envelopeId is not null)
        {
            var ack = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { envelope_id = envelopeId }));
            await ws.SendAsync(new ArraySegment<byte>(ack), WebSocketMessageType.Text, true, ct);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
