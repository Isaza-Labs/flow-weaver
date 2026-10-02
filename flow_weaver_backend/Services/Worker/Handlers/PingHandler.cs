using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Sends ICMP echo requests. Input: `{ "host": "<ip-or-hostname>",
// "count": 4, "timeout_ms": 3000 }`. If `host` is omitted and the step is
// scoped to a device, the handler resolves `Device.IpAddress` so users can
// add a ping node and just pick target devices in the run dialog.
//
// Safe: validates host with IPAddress.TryParse or DNS — never shells out.
public sealed class PingHandler : ISnippetHandler
{
    public string Type => "ping";

    private readonly IDeviceRepository _devices;
    private readonly ILogger<PingHandler> _logger;

    public PingHandler(IDeviceRepository devices, ILogger<PingHandler> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var input = request.InputPayload;

        string? host = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("host", out var h)
                ? h.GetString()
                : null;

        // Fall back to the step's target device so ping-on-inventory "just
        // works" without forcing users to template {{ device.ip }} into
        // every node config.
        Guid? targetId = request.DeviceId;

        // Belt-and-braces: the executor stamps the primary target onto
        // StepRun.DeviceId, but older step_run rows (and historical agent
        // flows) only carry the list under InputPayload._targets. Read the
        // first UUID from there as a secondary fallback so existing runs
        // keep working after an upgrade.
        if (!targetId.HasValue
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("_targets", out var targets)
            && targets.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in targets.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && Guid.TryParse(entry.GetString(), out var parsed))
                {
                    targetId = parsed;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(host) && targetId.HasValue)
        {
            var device = await _devices.GetByIdAsync(targetId.Value, activeOnly: false, tracking: false, ct);
            host = device?.IpAddress;
        }

        // `device` names an inventory device rather than an address
        // (snippets/SPEC.md `ping`). Resolving it here is what keeps the probe
        // pointed at the box the author meant: handed straight to the prober,
        // an inventory name would be DNS-resolved to whatever a permissive
        // resolver answers.
        if (string.IsNullOrWhiteSpace(host)
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("device", out var namedEl)
            && namedEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(namedEl.GetString()))
        {
            var wanted = namedEl.GetString()!.Trim();
            var named = Guid.TryParse(wanted, out var wantedId)
                ? await _devices.FindByIdUnscopedAsync(wantedId, ct)
                : await _devices.FindActiveByNameAsync(wanted, ct);
            if (named is null)
                return Fail($"not_found: no inventory device named '{wanted}'");
            host = named.IpAddress;
        }

        if (string.IsNullOrWhiteSpace(host))
            return Fail("cannot resolve ping host — provide input.host or run the step against a device");

        // Strip CIDR mask if present: NetBox and plenty of hand-entered
        // inventories store "172.30.0.11/16" as the IP. `Ping.SendPingAsync`
        // interprets that as a hostname literally and DNS-resolves (usually
        // to an external IP if your resolver is permissive), so the ping
        // either times out or goes to the wrong place. Drop whatever comes
        // after the slash so only the host portion remains.
        var slashIdx = host.IndexOf('/');
        if (slashIdx > 0) host = host.Substring(0, slashIdx);

        if (!IPAddress.TryParse(host, out _))
        {
            try { await Dns.GetHostEntryAsync(host, ct); }
            catch { return Fail($"host '{host}' could not be resolved"); }
        }

        var count = input.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 4;
        var timeoutMs = input.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var tv) ? tv : 3000;
        count = Math.Clamp(count, 1, 20);
        timeoutMs = Math.Clamp(timeoutMs, 100, 30000);

        _logger.LogDebug(
            "worker.ping.start step_run_id={StepRunId} device_id={DeviceId} count={Count} timeout_ms={TimeoutMs}",
            request.StepRunId, targetId, count, timeoutMs);

        using var pinger = new Ping();
        var rtts = new List<long>();
        var received = 0;
        var logs = new System.Text.StringBuilder();

        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            try
            {
                var reply = await pinger.SendPingAsync(host, timeoutMs);
                logs.AppendLine($"seq={i} status={reply.Status} rtt={reply.RoundtripTime}ms");
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    rtts.Add(reply.RoundtripTime);
                }
            }
            catch (PingException ex)
            {
                logs.AppendLine($"seq={i} error={ex.InnerException?.Message ?? ex.Message}");
            }
        }

        var success = received > 0;
        var avgRtt = rtts.Count > 0 ? rtts.Average() : 0;
        var packetLoss = count > 0 ? (double)(count - received) / count : 0;

        var output = JsonSerializer.SerializeToElement(new
        {
            success,
            rtt_avg_ms = Math.Round(avgRtt, 2),
            packets_sent = count,
            packets_received = received,
            raw_output = logs.ToString().TrimEnd(),
        });

        if (success)
        {
            _logger.LogInformation(
                "worker.ping.ok step_run_id={StepRunId} device_id={DeviceId} rtt_ms={RttMs} packet_loss={PacketLoss} packets_sent={PacketsSent} packets_received={PacketsReceived}",
                request.StepRunId, targetId, Math.Round(avgRtt, 2), Math.Round(packetLoss, 4), count, received);
        }
        else
        {
            _logger.LogError(
                "worker.ping.failed step_run_id={StepRunId} device_id={DeviceId} packet_loss={PacketLoss} packets_sent={PacketsSent} packets_received={PacketsReceived}",
                request.StepRunId, targetId, Math.Round(packetLoss, 4), count, received);
        }

        return new SnippetResult
        {
            // A reachability probe reads; it never mutates. Same verdict as the contract.
            Change = StepChange.Unchanged,
            Success = success,
            Output = output,
            Logs = logs.ToString(),
            Error = success ? string.Empty : $"ping failed: {received}/{count} replies",
        };
    }

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
