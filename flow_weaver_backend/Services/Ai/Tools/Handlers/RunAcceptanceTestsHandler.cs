using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Identity;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// S16 — Harness lesson 10: golden-path E2E tests bound to a workflow.
//
// Scope of this handler (intentionally narrow):
//   - Enumerate WorkflowAcceptanceTest rows for the workflow.
//   - For each test, find the most recent WorkflowRun whose Status is
//     RunStatus.Completed AND whose InputPayload matches the test's
//     Inputs (every top-level key in Inputs must match).
//   - Build a {node_id → output_payload} map from that run's StepRuns
//     so assertions can navigate `steps.<node_id>.output.<field>`.
//   - Evaluate the assertions and update LastStatus / LastRunId.
//
// What this handler does NOT do (left for the next iteration):
//   - Enqueue a fresh run if no matching WorkflowRun exists. Today the
//     operator triggers `run_workflow` with the test inputs first, then
//     this handler grades the result. Wiring it to the executor's
//     enqueue path needs a per-test isolation pass against QA Lab
//     devices that is out of scope for the S16 backbone.
//
// Assertion kinds supported in S16:
//   - status_equals     : run.Status == expected ("completed", "failed", ...)
//   - step_succeeded    : step <path> is present and Status == "completed"
//   - step_failed       : step <path> is present and Status == "failed"
//   - step_output_equals: steps.<node>.output.<dot-path> raw JSON == expected
//   - step_output_contains: dot-path resolves to a string containing expected
//
// LastStatus semantics:
//   - "passed"  — all assertions matched.
//   - "failed"  — assertions didn't match the run.
//   - "error"   — couldn't grade (no candidate run / malformed test).
//   - "skipped" — test inactive (not surfaced here because we filter IsActive).
public sealed class RunAcceptanceTestsHandler : IToolHandler
{
    public string Name => "run_acceptance_tests";

    public string Description =>
        "Tier: elevated_confirm. Evaluate the workflow's acceptance tests against the most " +
        "recent matching successful WorkflowRun. Updates each test's LastStatus. Use this " +
        "AFTER run_workflow with the test inputs so the agent can attest to E2E correctness " +
        "before promote_workflow.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["workflow_id"],
          "properties": {
            "workflow_id": { "type": "string", "format": "uuid" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IRepository<WorkflowModel> _workflows;
    private readonly IWorkflowAcceptanceTestRepository _tests;
    private readonly IWorkflowRunRepository _runs;
    private readonly IStepRunRepository _steps;
    private readonly ICurrentUser _caller;
    private readonly ILogger<RunAcceptanceTestsHandler> _logger;

    public RunAcceptanceTestsHandler(
        IRepository<WorkflowModel> workflows,
        IWorkflowAcceptanceTestRepository tests,
        IWorkflowRunRepository runs,
        IStepRunRepository steps,
        ICurrentUser caller,
        ILogger<RunAcceptanceTestsHandler> logger)
    {
        _workflows = workflows;
        _tests = tests;
        _runs = runs;
        _steps = steps;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("workflow_id", out var widEl)
            || widEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(widEl.GetString(), out var workflowId))
        {
            _logger.LogWarning("ai.tool.run_acceptance_tests.validation_failed reason=workflow_id_required");
            return JsonSerializer.SerializeToElement(new { error = "workflow_id (uuid) is required" });
        }

        var wf = await _workflows.GetByIdAsync(workflowId, activeOnly: true, tracking: false, ct);
        if (wf is null)
        {
            _logger.LogWarning("ai.tool.run_acceptance_tests.not_found workflow_id={WorkflowId}", workflowId);
            return JsonSerializer.SerializeToElement(new { error = "workflow not found" });
        }

        // Tracked — the grader mutates each test's last-run fields and saves.
        var tests = await _tests.ListTrackedByWorkflowAsync(workflowId, ct);

        if (tests.Count == 0)
        {
            _logger.LogInformation(
                "ai.tool.run_acceptance_tests.empty workflow_id={WorkflowId}",
                workflowId);
            return JsonSerializer.SerializeToElement(new
            {
                workflow_id = workflowId,
                test_count = 0,
                all_passed = true,
                results = Array.Empty<object>(),
                note = "no acceptance tests are defined for this workflow",
            });
        }

        // Grading window: only runs in the last 24h are candidates. A test
        // run from yesterday against a different DAG would be misleading
        // evidence; if the operator hasn't triggered a fresh run, the test
        // grades as "error" (no_matching_run).
        var since = DateTime.UtcNow.AddHours(-24);
        var candidateRuns = await _runs.ListRecentCompletedAsync(workflowId, since, 50, ct);

        // Pre-fetch step outputs for the candidate runs in one query so
        // we don't N+1 across tests. Only runs we may actually use.
        var candidateRunIds = candidateRuns.Select(r => r.WorkflowRunId).ToList();
        var stepRows = (await _steps.ListOutputsByRunIdsAsync(candidateRunIds, ct))
            .Select(s => new StepOutputRow(s.RunId, s.NodeId, s.Status, s.Output))
            .ToList();
        var stepsByRun = stepRows
            .GroupBy(s => s.RunId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var results = new List<object>();
        var now = DateTime.UtcNow;
        var allPassed = true;

        foreach (var test in tests)
        {
            var match = candidateRuns.FirstOrDefault(r => InputsMatch(test.Inputs, r.InputPayload));

            if (match is null)
            {
                test.LastStatus = WorkflowAcceptanceTestStatus.Error;
                test.LastRunAt = now;
                test.LastFailures = JsonSerializer.SerializeToElement(new[]
                {
                    new { reason = "no_matching_run", hint = "trigger run_workflow with these inputs first" },
                });
                test.UpdatedAt = now;
                allPassed = false;
                results.Add(new
                {
                    test_id = test.WorkflowAcceptanceTestId,
                    name = test.Name,
                    status = test.LastStatus,
                    reason = "no_matching_run",
                });
                continue;
            }

            var steps = stepsByRun.TryGetValue(match.WorkflowRunId, out var s) ? s : new List<StepOutputRow>();
            var failures = EvaluateAssertions(test.Assertions, match.Status, steps);

            test.LastRunId = match.WorkflowRunId;
            test.LastRunAt = now;
            test.UpdatedAt = now;
            if (failures.Count == 0)
            {
                test.LastStatus = WorkflowAcceptanceTestStatus.Passed;
                test.LastFailures = JsonSerializer.SerializeToElement(Array.Empty<object>());
                results.Add(new
                {
                    test_id = test.WorkflowAcceptanceTestId,
                    name = test.Name,
                    status = test.LastStatus,
                    run_id = match.WorkflowRunId,
                });
            }
            else
            {
                test.LastStatus = WorkflowAcceptanceTestStatus.Failed;
                test.LastFailures = JsonSerializer.SerializeToElement(failures);
                allPassed = false;
                results.Add(new
                {
                    test_id = test.WorkflowAcceptanceTestId,
                    name = test.Name,
                    status = test.LastStatus,
                    run_id = match.WorkflowRunId,
                    failures,
                });
            }
        }

        try
        {
            await _tests.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ai.tool.run_acceptance_tests.persist_failed workflow_id={WorkflowId}",
                workflowId);
            // Continue — the in-memory verdict is still returned to the agent.
        }

        _logger.LogInformation(
            "ai.tool.run_acceptance_tests.ok workflow_id={WorkflowId} test_count={TestCount} all_passed={AllPassed}",
            workflowId, tests.Count, allPassed);

        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = workflowId,
            test_count = tests.Count,
            all_passed = allPassed,
            results,
        });
    }

    // Lightweight match: the test's Inputs is an object; a candidate run
    // matches if every top-level key in Inputs is present in the run's
    // InputPayload with byte-equal raw JSON. Conservative — false
    // negatives are recoverable (trigger a fresh run); false positives
    // would silently grade against the wrong run.
    internal static bool InputsMatch(JsonElement testInputs, JsonElement runInputs)
    {
        if (testInputs.ValueKind != JsonValueKind.Object) return true;
        if (runInputs.ValueKind != JsonValueKind.Object) return false;

        foreach (var prop in testInputs.EnumerateObject())
        {
            if (!runInputs.TryGetProperty(prop.Name, out var runValue)) return false;
            if (prop.Value.GetRawText() != runValue.GetRawText()) return false;
        }
        return true;
    }

    internal static List<object> EvaluateAssertions(
        JsonElement assertions, string runStatus, IReadOnlyList<StepOutputRow> steps)
    {
        var failures = new List<object>();
        if (assertions.ValueKind != JsonValueKind.Array) return failures;

        foreach (var a in assertions.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            var kind = StringOrNull(a, "kind");
            var path = StringOrNull(a, "path");

            switch (kind)
            {
                case "status_equals":
                {
                    var expected = StringOrNull(a, "expected");
                    if (expected is null || !string.Equals(runStatus, expected, StringComparison.Ordinal))
                        failures.Add(new { kind, expected, actual = runStatus });
                    break;
                }

                case "step_succeeded":
                case "step_failed":
                {
                    if (path is null)
                    {
                        failures.Add(new { kind, reason = "path_required" });
                        break;
                    }
                    var step = steps.FirstOrDefault(s => string.Equals(s.NodeId, path, StringComparison.Ordinal));
                    if (step is null)
                    {
                        failures.Add(new { kind, path, reason = "step_not_found" });
                        break;
                    }
                    var expected = kind == "step_succeeded" ? StepStatus.Completed : StepStatus.Failed;
                    if (!string.Equals(step.Status, expected, StringComparison.Ordinal))
                        failures.Add(new { kind, path, expected, actual = step.Status });
                    break;
                }

                case "step_output_equals":
                {
                    var (failure, _) = NavigateStepOutput(a, steps);
                    if (failure is not null) failures.Add(failure);
                    break;
                }

                case "step_output_contains":
                {
                    var (failure, actual) = NavigateStepOutput(a, steps, captureValue: true);
                    var expected = StringOrNull(a, "expected");
                    if (failure is not null) { failures.Add(failure); break; }
                    var actualStr = actual.ValueKind == JsonValueKind.String
                        ? actual.GetString()
                        : actual.GetRawText();
                    if (expected is null
                        || string.IsNullOrEmpty(actualStr)
                        || !actualStr.Contains(expected, StringComparison.Ordinal))
                    {
                        failures.Add(new { kind = "step_output_contains", expected, actual = actualStr });
                    }
                    break;
                }

                default:
                    failures.Add(new { kind, reason = "unknown_assertion_kind" });
                    break;
            }
        }
        return failures;
    }

    // Resolves an assertion path of the form `<node_id>.<field>.<field>`
    // against the {node_id → output_payload} map. Returns (failure?, value).
    // `captureValue=true` makes the navigated JsonElement available so
    // `step_output_contains` can run its substring check.
    private static (object? failure, JsonElement value) NavigateStepOutput(
        JsonElement assertion,
        IReadOnlyList<StepOutputRow> steps,
        bool captureValue = false)
    {
        var path = StringOrNull(assertion, "path");
        if (path is null)
            return (new { kind = StringOrNull(assertion, "kind"), reason = "path_required" }, default);

        var parts = path.Split('.', 2);
        var nodeId = parts[0];
        var rest = parts.Length > 1 ? parts[1] : null;

        var step = steps.FirstOrDefault(s => string.Equals(s.NodeId, nodeId, StringComparison.Ordinal));
        if (step is null)
            return (new { kind = StringOrNull(assertion, "kind"), path, reason = "step_not_found" }, default);

        var target = rest is null ? step.Output : NavigatePath(step.Output, rest);

        if (StringOrNull(assertion, "kind") == "step_output_equals")
        {
            var expected = assertion.TryGetProperty("expected", out var e) ? e : default;
            if (target.ValueKind == JsonValueKind.Undefined || target.GetRawText() != expected.GetRawText())
                return (new { kind = "step_output_equals", path, expected, actual = ToFailureValue(target) }, target);
            return (null, target);
        }

        return (null, captureValue ? target : default);
    }

    // Dot-path navigator over JSON objects. Returns `default` when any
    // segment misses. Doesn't support array indexes; the assertion docs
    // call this out.
    internal static JsonElement NavigatePath(JsonElement root, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        foreach (var part in parts)
        {
            if (cur.ValueKind != JsonValueKind.Object) return default;
            if (!cur.TryGetProperty(part, out var next)) return default;
            cur = next;
        }
        return cur;
    }

    private static string? StringOrNull(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static object? ToFailureValue(JsonElement el) =>
        el.ValueKind == JsonValueKind.Undefined ? null : (object)el;

    internal sealed record StepOutputRow(Guid RunId, string NodeId, string Status, JsonElement Output);
}
