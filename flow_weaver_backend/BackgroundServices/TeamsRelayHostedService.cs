using System.Text;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Messaging;
using Microsoft.Azure.Relay;

namespace flow_weaver_backend.BackgroundServices;

// Azure Relay connector for Teams — the Bot Framework's answer to Slack's
// Socket Mode, which Teams itself does not offer.
//
// Azure Bot Service only ever DELIVERS activities by POSTing to the bot's
// messaging endpoint; there is no API that hands us an outbound socket the way
// Slack's apps.connections.open does. Azure Relay closes that gap from the
// other side: this process opens an outbound WebSocket control channel to
// {namespace}.servicebus.windows.net and the Relay forwards HTTP requests over
// it. The Azure Bot's messaging endpoint becomes the Relay's public URL, and
// the deployment needs no ingress, no port-forward and no public IP.
//
// Structure deliberately mirrors SlackSocketModeHostedService: a reconcile loop
// keeps one listener per configured channel in sync with the DB, and each
// listener self-heals with backoff.
//
// Trust boundary — the one real difference from Slack. A Socket Mode event is
// trusted because the socket itself is authenticated, so that path calls
// ReceiveVerifiedAsync. Here the Relay is only a transport: the request that
// comes out of it is the same Bot Framework POST, carrying the same JWT, and it
// goes through the ordinary ReceiveAsync so issuer, audience, signature and the
// serviceurl claim are all still checked. Relay-level sender auth is therefore
// optional; the JWT is what actually guards the channel.
public sealed class TeamsRelayHostedService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    // The Relay drops a request whose response takes longer than 60s. Ingest
    // only enqueues, so this is a wide margin that exists to guarantee we
    // answer at all rather than to bound normal work.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeamsRelayHostedService> _logger;

    private readonly Dictionary<Guid, Listener> _listeners = new();
    // Channels already reported as webhook-only, so the notice is logged once
    // per channel rather than on every 30 s reconcile pass.
    private readonly HashSet<Guid> _webhookOnlyLogged = new();

    public TeamsRelayHostedService(
        IServiceScopeFactory scopeFactory, ILogger<TeamsRelayHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private sealed record Listener(string ConnectionString, CancellationTokenSource Cts, Task Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TeamsRelay started — reconcile every {Seconds}s", (int)ReconcileInterval.TotalSeconds);

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
                _logger.LogError(ex, "teams.relay.reconcile_failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); } catch { break; }
        }

        foreach (var listener in _listeners.Values) listener.Cts.Cancel();
        _listeners.Clear();
        _logger.LogInformation("TeamsRelay shutting down");
    }

    // Bring the live listeners in line with the DB: start newly configured
    // channels, stop the ones that vanished or got disabled, restart the ones
    // whose connection string changed.
    private async Task ReconcileAsync(CancellationToken ct)
    {
        Dictionary<Guid, string> desired = new();
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<IMessagingChannelRepository>();
            var crypto = scope.ServiceProvider.GetRequiredService<ICredentialEncryptionService>();
            var list = await channels.ListEnabledByProviderAllTenantsAsync(MessagingChannel.ProviderTeams, ct);
            foreach (var c in list)
            {
                if (c.EncryptedAppToken is not { Length: > 0 })
                {
                    // A Teams channel without a Relay connection string is
                    // legitimately webhook-only — not a warning. But say it
                    // ONCE: an operator who expected the Relay path otherwise
                    // sees "listener never started" with zero log evidence,
                    // and that silence reads as a mystery instead of as this
                    // one-line answer.
                    if (_webhookOnlyLogged.Add(c.MessagingChannelId))
                        _logger.LogInformation(
                            "teams.relay.webhook_only channel={Channel} — no Azure Relay connection "
                            + "string configured; the channel only receives via its public webhook",
                            c.MessagingChannelId);
                    continue;
                }
                // It has a string now — if it is later cleared, re-announce.
                _webhookOnlyLogged.Remove(c.MessagingChannelId);
                var connectionString = crypto.Decrypt(c.EncryptedAppToken);
                if (!string.IsNullOrWhiteSpace(connectionString))
                    desired[c.MessagingChannelId] = connectionString;
            }
        }

        foreach (var (id, listener) in _listeners.ToList())
        {
            if (!desired.TryGetValue(id, out var cs)
                || !string.Equals(cs, listener.ConnectionString, StringComparison.Ordinal))
            {
                listener.Cts.Cancel();
                _listeners.Remove(id);
                _logger.LogInformation("teams.relay.stop channel={Channel}", id);
            }
        }

        foreach (var (id, cs) in desired)
        {
            if (_listeners.ContainsKey(id)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = RunListenerAsync(id, cs, cts.Token);
            _listeners[id] = new Listener(cs, cts, task);
            _logger.LogInformation("teams.relay.start channel={Channel}", id);
        }
    }

    // One channel's listener: open → serve until it drops → reopen with capped
    // exponential backoff, until cancelled.
    private async Task RunListenerAsync(Guid channelId, string connectionString, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            HybridConnectionListener? listener = null;
            try
            {
                listener = new HybridConnectionListener(connectionString);
                // The handler swallows its own failures, so discarding the task
                // here loses nothing — and keeps this off `async void`.
                listener.RequestHandler = context => _ = HandleRequestAsync(channelId, context, ct);

                await listener.OpenAsync(ct);
                attempt = 0;
                _logger.LogInformation(
                    "teams.relay.connected channel={Channel} address={Address}", channelId, listener.Address);

                // OpenAsync returns as soon as the control channel is up; the
                // listener then serves requests on its own. Park here until
                // cancellation, and let the finally close it cleanly.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "teams.relay.error channel={Channel} attempt={Attempt}", channelId, attempt);
            }
            finally
            {
                if (listener is not null)
                {
                    // Never let a failed close mask the original error or stall
                    // the retry loop.
                    try { await listener.CloseAsync(CancellationToken.None); } catch { }
                }
            }

            if (ct.IsCancellationRequested) break;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            attempt++;
            try { await Task.Delay(delay, ct); } catch { break; }
        }

        _logger.LogInformation("teams.relay.closed channel={Channel}", channelId);
    }

    // Serves one relayed activity. Runs on the Relay's callback, so it must
    // never throw: an unhandled exception here would leave the sender hanging
    // until the Relay's own 60s timeout.
    private async Task HandleRequestAsync(
        Guid channelId, RelayedHttpListenerContext context, CancellationToken ct)
    {
        var status = 500;
        var body = "internal error";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            var request = await ToMessagingRequestAsync(context, timeout.Token);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var ingest = scope.ServiceProvider.GetRequiredService<IMessagingIngestService>();
            // ReceiveAsync, not ReceiveVerifiedAsync: the Relay is transport,
            // not authentication. The Bot Framework JWT rode through it and is
            // still the thing that proves the activity is genuine.
            var outcome = await ingest.ReceiveAsync(
                MessagingChannel.ProviderTeams, channelId, request, timeout.Token);
            status = outcome.StatusCode;
            body = outcome.Body ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "teams.relay.dispatch_failed channel={Channel}", channelId);
        }

        try
        {
            context.Response.StatusCode = (System.Net.HttpStatusCode)status;
            context.Response.Headers["Content-Type"] = "text/plain";
            var bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes, CancellationToken.None);
            await context.Response.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "teams.relay.respond_failed channel={Channel}", channelId);
        }
    }

    // Unpacks the Relay's context into the primitives the mapping works on.
    // Kept trivial: everything worth testing lives in the overload below, which
    // takes no Relay types (RelayedHttpListenerContext cannot be constructed
    // outside the SDK).
    private static Task<MessagingHttpRequest> ToMessagingRequestAsync(
        RelayedHttpListenerContext context, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in context.Request.Headers.AllKeys)
        {
            if (key is null) continue;
            headers[key] = context.Request.Headers[key] ?? string.Empty;
        }

        return ToMessagingRequestAsync(
            context.Request.HttpMethod,
            headers,
            context.Request.Url,
            context.Request.HasEntityBody ? context.Request.InputStream : null,
            ct);
    }

    // Translates a relayed request into the provider-agnostic shape the ingest
    // pipeline takes. This is where the Bot Framework JWT either survives the
    // extra hop or quietly does not, so it is written against plain values and
    // covered by tests rather than needing an Azure Relay namespace to exercise.
    internal static async Task<MessagingHttpRequest> ToMessagingRequestAsync(
        string? method, IReadOnlyDictionary<string, string>? headers,
        Uri? url, Stream? body, CancellationToken ct)
    {
        byte[] bodyBytes = Array.Empty<byte>();
        if (body is not null)
        {
            using var ms = new MemoryStream();
            await body.CopyToAsync(ms, ct);
            bodyBytes = ms.ToArray();
        }

        // Header lookup must stay case-insensitive whatever the Relay handed
        // us: the providers ask for "Authorization" by name.
        var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
            foreach (var (key, value) in headers)
                if (!string.IsNullOrEmpty(key)) headerMap[key] = value ?? string.Empty;

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawQuery = url?.Query;
        if (!string.IsNullOrEmpty(rawQuery))
        {
            foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                var name = Uri.UnescapeDataString(idx < 0 ? pair : pair[..idx]);
                var value = idx < 0 ? string.Empty : Uri.UnescapeDataString(pair[(idx + 1)..]);
                query[name] = value;
            }
        }

        return new MessagingHttpRequest
        {
            // Default to POST when the Relay reports no verb: an activity that
            // arrived without one must still face verification, not be waved
            // through as if it were a GET probe.
            Method = string.IsNullOrEmpty(method) ? "POST" : method,
            Body = bodyBytes,
            Headers = headerMap,
            Query = query,
        };
    }
}
