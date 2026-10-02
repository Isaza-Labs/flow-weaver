namespace flow_weaver_backend.Services.Worker.Simulation;

// Device-simulation / load-test mode. When enabled, the
// device-touching leaf handlers (ping / ssh / ansible) are swapped for fakes
// that emit realistic output after a synthetic latency and NEVER perform real
// network I/O — so a staging box can be scaled to thousands of synthetic
// devices to measure the orchestrator/queue/DB capacity without any hardware.
//
// OFF by default. Boot is refused when this is on AND the box runs production
// workflows (see SimulationGuard) — device simulation must never run on a tier
// that manages real production devices.
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    public bool Enabled { get; set; } = false;

    // Base synthetic per-step latency (ms) the fake device handlers sleep for.
    public int LatencyMs { get; set; } = 50;

    // +/- uniform random jitter (ms) added to LatencyMs, so p95 curves aren't
    // perfectly flat. 0 disables jitter.
    public int LatencyJitterMs { get; set; } = 20;

    // Fraction [0..1] of simulated steps that return a failure, to exercise the
    // error-rate curve and the retry/failure paths under load. 0 = all succeed.
    public double FailureRate { get; set; } = 0.0;
}

// Boot-time safety gate for the simulation mode.
public static class SimulationGuard
{
    public const string ProductionWorkerEnvironment = "production";

    // Refuse to start with simulation on when this process runs production
    // workflows — faking device I/O there would silently "succeed" real ops
    // against real production devices. Throw so the deploy fails loudly rather
    // than booting into an unsafe state.
    public static void ThrowIfUnsafe(bool simulationEnabled, string? workerEnvironment)
    {
        if (simulationEnabled
            && string.Equals(workerEnvironment, ProductionWorkerEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Simulation:Enabled=true is refused when Workflow:WorkerEnvironment=production. "
                + "Device simulation fakes ping/ssh/ansible I/O and must never run on a tier that "
                + "manages real production devices. Run the load test on a dev-sandbox / qa-lab staging box.");
        }
    }
}
