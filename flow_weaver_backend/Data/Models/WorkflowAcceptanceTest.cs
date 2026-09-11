using System.Text.Json;

namespace flow_weaver_backend.Models;

// S16 — Harness lesson 10: golden-path E2E tests bound to a workflow.
// Each row defines a synthetic input + a set of assertions evaluated
// against the run's output. The runner executes the workflow against
// QA Lab devices (the executor's existing qa-lab restriction handles
// safety) and updates LastStatus.
//
// Assertions are JSON. Format: [{kind, path, op, expected}]. The runner
// (RunAcceptanceTestsHandler) interprets a small set of kinds:
//   - status_equals: expected ∈ {succeeded, failed}
//   - output_equals: path is a JMESPath; expected is any literal
//   - output_contains: path is a JMESPath; expected is a string
//   - step_succeeded / step_failed: path is a node id
//
// LastStatus is denormalized for fast list queries; the per-run history
// lives in the run/step trace rows the executor already emits.
public class WorkflowAcceptanceTest : BaseModel
{
    public Guid WorkflowAcceptanceTestId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement Inputs { get; set; } = default;
    public JsonElement Assertions { get; set; } = default;

    // Denormalized cache of the latest run outcome. Reset to null on
    // workflow structural edits via WorkflowService.UpdateAsync so the
    // "all tests green" gate refuses stale evidence.
    public string? LastStatus { get; set; }
    public Guid? LastRunId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public JsonElement LastFailures { get; set; } = default;
}

public static class WorkflowAcceptanceTestStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Error = "error";
    public const string Skipped = "skipped";
    public const string Running = "running";
}
