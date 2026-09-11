using System.Text.Json;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Worker.Simulation;

// Fake device handler for load testing. Registered (in place of the real
// handler, under the SAME Type string) only when Simulation:Enabled — the
// worker resolves handlers by Type via FirstOrDefault, so these transparently
// replace ping / ssh / ansible with zero real network I/O. Emits a realistic
// output payload after a configurable synthetic latency, and can inject a
// configurable failure rate to shape the error-rate curve.
//
// The real handlers are never even constructed when simulation is on (Program.cs
// registers one set or the other), so there is no path to accidental real I/O.
public abstract class SimulatedDeviceHandler : ISnippetHandler
{
    private readonly SimulationOptions _options;

    protected SimulatedDeviceHandler(IOptions<SimulationOptions> options)
        => _options = options.Value;

    public abstract string Type { get; }

    // Idempotency floor mirrors the real handler so a simulated workflow still
    // passes the same promotion/rollback analysis it would in production.
    public virtual IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var jitter = _options.LatencyJitterMs > 0
            ? Random.Shared.Next(-_options.LatencyJitterMs, _options.LatencyJitterMs + 1)
            : 0;
        var delayMs = Math.Max(0, _options.LatencyMs + jitter);
        if (delayMs > 0)
            await Task.Delay(delayMs, ct);

        if (_options.FailureRate > 0 && Random.Shared.NextDouble() < _options.FailureRate)
        {
            return new SnippetResult
            {
                // Simulated: it touched nothing, which is the whole point of the handler.
                Change = StepChange.Unchanged,
                Success = false,
                Error = "simulated device failure (load test)",
                Logs = $"[sim] {Type} FAILED after {delayMs}ms device={request.DeviceId}",
            };
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            simulated = true,
            type = Type,
            device_id = request.DeviceId,
            latency_ms = delayMs,
            ok = true,
        });
        return new SnippetResult
        {
            // Simulated: it slept and returned a shape. Reporting `Changed` here would put
            // a load test's fake steps into a real run's rollback plan.
            Change = StepChange.Unchanged,
            Success = true,
            Output = output,
            Logs = $"[sim] {Type} ok in {delayMs}ms device={request.DeviceId}",
        };
    }
}

public sealed class SimPingHandler : SimulatedDeviceHandler
{
    public SimPingHandler(IOptions<SimulationOptions> options) : base(options) { }
    public override string Type => "ping";
}

public sealed class SimSshHandler : SimulatedDeviceHandler
{
    public SimSshHandler(IOptions<SimulationOptions> options) : base(options) { }
    public override string Type => "ssh";
    public override IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;
}

public sealed class SimAnsibleHandler : SimulatedDeviceHandler
{
    public SimAnsibleHandler(IOptions<SimulationOptions> options) : base(options) { }
    public override string Type => "ansible_playbook";
    public override IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;
}
