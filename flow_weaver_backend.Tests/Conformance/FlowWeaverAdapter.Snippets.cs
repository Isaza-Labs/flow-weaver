using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;

namespace flow_weaver_backend.Tests.Conformance;

// Family `snippets` (conformance/snippets/SPEC.md).
//
// Three forms, and the adapter answers the two that need no infrastructure:
//
//   { type, input }               -> { normalized_input }
//       The payload after alias normalisation, before execution. Answered by
//       `PayloadAliases.Normalize` — the single place this product decides the
//       canonical-vs-alias question, and the one the handlers themselves call
//       (`AnsibleHandler` line 52 and its siblings). Not a copy of that rule: the rule.
//
//   { type, declared_idempotency, config_overrides? } -> { effective_idempotency }
//       Answered by `WorkflowRollbackAnalyzer.EffectiveKind`, which is the one function
//       that decides a tier here — the same one the executor and the rollback analyser
//       both go through.
//
//   { type, probe } -> { sent } | { output }
//       NOT ANSWERED YET, and it fails rather than being skipped. See below.
//
// ── Why the probe form is left failing rather than faked ────────────────────
//
// A probe vector asserts what a handler CAUSED — the HTTP request it made, the message it
// posted, the tool it actually invoked — not what it returned. That distinction is the
// entire point of the form: a correct run and a silently broken one produce identical
// output, and only the outbound side tells them apart.
//
// Answering it needs each handler run against an intercepted transport, which is real
// fixture work. Returning the
// vector's own probe payload back as the answer would produce twelve green vectors that
// assert nothing — a vector that carries a payload and then checks that same payload has
// those fields agrees with itself and can never fail.
//
// So they fail, visibly, and the floor records what is actually checked.
public sealed partial class FlowWeaverAdapter
{
    private static JsonElement? Snippets(JsonElement input)
    {
        var type = Str(input, "type");
        if (type is null) return null;

        // Presence, not value: `declared_idempotency: null` is a vector saying the snippet
        // declares nothing, which is a different question from a vector that does not ask
        // about tiers at all.
        if (input.TryGetProperty("declared_idempotency", out var declared))
            return Tier(type, declared, PropOrNull(input, "config_overrides"));

        // Unanswered on purpose. Returning null makes the runner FAIL the vector, which is
        // the kit's rule: silence is not a skip.
        if (input.TryGetProperty("probe", out _)) return null;

        if (PropOrNull(input, "input") is not { } payload) return null;

        return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            ["normalized_input"] = PayloadAliases.Normalize(type, payload),
        });
    }

    // The tier that actually applies, through the one function that decides it.
    //
    // The handler floor comes from the registered handler for the type, exactly as
    // production resolves it; an unregistered type is scored the conservative
    // `requires_compensation`, which is what the executor does too.
    private static JsonElement Tier(string type, JsonElement declared, JsonElement? configOverrides)
    {
        var floor = HandlerFloors.TryGetValue(type, out var f) ? f : IdempotencyKind.RequiresCompensation;

        var snippetTier = declared.ValueKind == JsonValueKind.String ? declared.GetString() : null;
        var nodeTier = configOverrides is { ValueKind: JsonValueKind.Object } o
                       && o.TryGetProperty("idempotency", out var v)
                       && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

        return JsonSerializer.SerializeToElement(new
        {
            effective_idempotency = RunOutcomeCalculator.ToWire(
                WorkflowRollbackAnalyzer.EffectiveKind(floor, snippetTier, nodeTier)),
        });
    }

    // Each shipped handler's DefaultIdempotency, read off the handlers themselves rather
    // than transcribed — a table copied by hand is a second source of truth, and this one
    // would drift the first time a floor moved.
    private static readonly Dictionary<string, IdempotencyKind> HandlerFloors =
        typeof(ISnippetHandler).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISnippetHandler).IsAssignableFrom(t))
            .Select(t =>
            {
                try { return Activator.CreateInstance(t, nonPublic: true) as ISnippetHandler; }
                catch { return null; }
            })
            .Where(h => h is not null)
            .GroupBy(h => h!.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()!.DefaultIdempotency, StringComparer.OrdinalIgnoreCase);
}
