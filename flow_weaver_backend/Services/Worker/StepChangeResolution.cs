using System.Text.Json;

namespace flow_weaver_backend.Services.Worker;

// Turns a handler's answer into the flag a step is recorded with.
//
// The same three-step rule as the reference engine's, and the same
// order, because it is the contract's rule rather than either product's:
//
//   1. A handler that MEASURED is believed. Nothing overrides it — an author who declares
//      `changes: false` on a step whose handler watched it write does not get to say so.
//   2. Otherwise the node declares, because for `ssh` and `mcp_call` the node is where the
//      action lives: the same snippet runs `show version` on one node and `configure
//      terminal` on another.
//   3. Otherwise the snippet declares, for the types whose CODE it carries — a python
//      script, a playbook.
//
// Nobody declaring is a DEFECT, not a default. It returns null, and the caller fails the
// step naming what to set and where. That is louder than the silence it replaces, which is
// the point: the old silence was answered by a guess.
internal static class StepChangeResolution
{
    internal static bool? Resolve(StepChange reported, bool? snippetDeclares, JsonElement stepInput)
    {
        if (reported == StepChange.Changed) return true;
        if (reported == StepChange.Unchanged) return false;
        if (Declared(stepInput) is { } onNode) return onNode;
        return snippetDeclares;
    }

    // The node's declaration.
    //
    // An engine may read `config_overrides.changes` off the node itself; here the step's
    // InputPayload IS that config, already merged and resolved by the orchestrator
    // (`WorkflowExecutor.MergePayloads`, where config_overrides wins). So the node still
    // wins over the snippet, which is the rule that matters. The one behavioural difference
    // is that a `changes` key on the RUN's input would also be seen — recorded here rather
    // than hidden, because a run-level input named `changes` is a collision worth knowing
    // about and not one worth a second column to prevent.
    //
    // Anything that is not literal true or false is not a declaration. A string "true"
    // silently accepted would be a declaration nobody made.
    internal static bool? Declared(JsonElement stepInput) =>
        stepInput.ValueKind == JsonValueKind.Object
        && stepInput.TryGetProperty("changes", out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    internal static string UndeclaredMessage(string snippetName, string snippetType, string nodeId) =>
        $"snippet '{snippetName}' is a '{snippetType}', whose handler cannot tell whether a step "
        + "changed anything — the author has to say. Set `changes` to true or false in node "
        + $"'{nodeId}'s config_overrides, or set it on the snippet itself.";
}
