using System.Text.Json;
using flow_weaver_backend.Services.Promotion;

namespace flow_weaver_backend.Tests.Conformance;

// Family `gate` (conformance/execution/SPEC.md — promotion).
//
//   { workflow_state, action, simulation: { schema_hash, ok } | null, current_schema_hash }
//     -> { http_status, code }
//
// Answered by `PromotionService.Decide`, which is the rule the promote endpoint runs. What the
// vectors actually pin is the ORDER of the checks, and the order is the substance: a stale
// check that ran before the failure check would tell an author to re-simulate a graph whose
// simulation had failed, and one that ran before the missing check would tell an author to
// re-simulate something they never simulated. Each wrong order produces a remedy that does not
// help, while still refusing the promotion — so a product could be "correct" about blocking and
// useless about why.
public sealed partial class FlowWeaverAdapter
{
    private static JsonElement? Gate(JsonElement input)
    {
        // Operation-level permission classification is a different question and no vector
        // authors it. One that did would FAIL here rather than vanish, which is the point.
        if (input.TryGetProperty("ops", out _)) return null;

        // Only the draft→qa transition carries a simulation gate. A vector asserting another
        // action is asserting something this adapter has not been taught, and must fail rather
        // than be answered by the one rule it happens to know.
        var action = Str(input, "action") ?? string.Empty;
        if (!action.StartsWith("promote:", StringComparison.Ordinal) ||
            !action.EndsWith("->qa", StringComparison.Ordinal))
            return null;

        var sim = input.TryGetProperty("simulation", out var s) && s.ValueKind == JsonValueKind.Object
            ? s
            : (JsonElement?)null;

        var result = PromotionService.Decide(
            simulationExists: sim is not null,
            simulationOk: sim is { } v && v.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            simulationHash: sim is { } h ? Str(h, "schema_hash") : null,
            currentHash: Str(input, "current_schema_hash"),
            issueCount: 0);

        return JsonSerializer.SerializeToElement(new
        {
            // 412 is what the endpoint returns for every one of these: the request was
            // well-formed and the workflow was not in a state that allows it.
            http_status = result.Ok ? 200 : 412,
            code = result.Reason,
        });
    }
}
