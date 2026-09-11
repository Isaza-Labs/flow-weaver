# Load / capacity testing — device scaling

Measures how a single FlowWeaver instance behaves as the managed-device count
grows (50 / 250 / 1000 / 5000) under a representative per-device workflow load,
and where it saturates. Since licensing moved to per-device (acta F1), this also
feeds the **commercial bands** — the saturation knee defines the per-instance
device ceiling for each tier.

The whole harness runs on **synthetic devices with faked device I/O**, so it
never touches real hardware and never needs the shared lab.

## Targets (fill in from product)

| Metric | Target |
|---|---|
| Run p95 latency | _TBD by product_ |
| Error rate | _TBD_ |
| Throughput (runs/hr) | _TBD_ |
| Device ceiling per instance | _the output of this test_ |

## Safety model

- **Simulation mode is off by default.** It's a boot-time DI swap: with
  `Simulation:Enabled=true`, the `ping` / `ssh` / `ansible_playbook` step
  handlers are replaced by fakes that emit realistic output after a synthetic
  latency and perform **zero** real network I/O. The real handlers are never
  constructed, so there is no path to accidental device access.
- **Boot is refused on a production tier.** If `Simulation:Enabled=true` and
  `Workflow:WorkerEnvironment=production`, startup throws (`SimulationGuard`). Run
  the load test only on a `dev-sandbox` / `qa-lab` staging box.
- The seed endpoint (`/api/admin/loadtest/*`) returns 403 unless simulation is on,
  so it can't pollute a real inventory.

## Run it

1. Stand up an **isolated staging** stack (its own DB — never the shared lab):
   ```bash
   # in deploy/.env
   SIMULATION_ENABLED=true
   SIMULATION_LATENCY_MS=50        # tune to model your real device round-trip
   SIMULATION_FAILURE_RATE=0       # raise to exercise the error-rate curve
   WORKER_ENVIRONMENT=dev-sandbox  # MUST NOT be production
   docker compose -f deploy/docker-compose.yml up -d
   ```
2. Drive the sweep:
   ```bash
   BASE_URL=http://localhost:8080 ADMIN_USER=admin ADMIN_PASS=admin \
     COUNTS="50 250 1000 5000" ./deploy/loadtest/run-loadtest.sh
   ```
   For each step it tears down, seeds N synthetic devices + a pool, fires one
   per-device ping fan-out run, waits for it, and records the curves to a CSV in
   `./loadtest-reports/`.

## What it measures & where the numbers come from

- **Run-level p95 / error-rate / throughput** — `GET /api/admin/metrics/slo`
  (computed by `SloComputeService` over `WorkflowRun.StartedAt/CompletedAt/Status`).
- **Per-step latency** — compute from `GET /api/run/{id}/steps`
  (`StepRun.StartedAt/CompletedAt`); one step_run per device per per_device node.
- **Wall-clock per run** — the script times each fan-out end to end.

Plot `device_count` vs `run_seconds` / `p95` / `error_rate`; the knee where the
curves bend up is the saturation point / the device ceiling for the band.

## Known bottlenecks & tuning knobs

The saturation point is dominated by FlowWeaver's orchestration, not the (faked)
device call — which is exactly the point of the sim. Likely order:

1. **Worker slots** — `Worker:MaxConcurrency` (default **10**). An *orchestrator*
   job holds a slot for the whole run; step jobs share the same pool, so step
   throughput starves under big fan-outs. Split tags across worker replicas
   (`Worker:Tags=["default"]` on some, `["orchestrator"]` on others) and raise
   `MaxConcurrency`.
2. **Orchestrator poll cost** — each active run re-reads all its `step_runs` every
   `Workflow:PollIntervalMs` (500 ms). A 5000-step run re-reads 5000 rows twice a
   second → DB read pressure grows with steps × concurrent runs.
3. **Per-step DB writes + queue inserts** — fan-out writes one `step_run` + one
   `jobs` row per device; 5000 devices = 5000 inserts + 5000 claim/complete
   updates per run.
4. **Orchestrator semaphore** — `Workflow:MaxConcurrentWorkflows` (default **20**),
   matters most with many concurrent runs.
5. **DB connection pool** — the queue opens the EF connection for its raw-SQL
   claim under high worker-replica counts.

Sweep these knobs across runs to find the configuration that pushes the ceiling
highest, and record hardware (CPU / RAM / Postgres sizing) alongside the curves —
the report is only meaningful with the hardware and knob settings noted.

## Teardown

The script tears down at the end; to clean up manually:
`DELETE /api/admin/loadtest/teardown` (removes all `loadtest`-marked devices,
pools, and the ping workflow/snippet).

## Scope note

This document + `deploy/loadtest/run-loadtest.sh` are the **tooling**. Producing
the actual approved curves and the commercial bands is a product/perf exercise:
it needs the staging hardware sizing and the success thresholds filled in above.
The in-repo unit tests (`SimulationHarnessTests`) only pin the harness mechanics
(fake handlers, the production guard, seeding) — not capacity numbers, which
require the real Postgres queue.
