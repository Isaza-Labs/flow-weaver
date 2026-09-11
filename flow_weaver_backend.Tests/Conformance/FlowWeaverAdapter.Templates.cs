using System.Globalization;
using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests.Conformance;

// Family `templates` (conformance/templates/SPEC.md).
//
// Two input forms, per adapters/README.md:
//
//   { outputs, input, device, run, consumer_target_mode, template } -> { resolved }
//   { context: { outputs, input, run, device }, condition }          -> { result }
//
// `resolved` is the template's value after resolution, or the string "unresolved" when a
// residual `{{ … }}` survived. §8's rule is that an unresolvable reference is left literal and
// the step then fails, so "a residual is left" is the observable a vector can assert without
// depending on how a product words the failure.
//
// Everything here is answered by the ENGINE, never rebuilt beside it:
//
//   - the device view comes from `WorkflowExecutor.BuildDeviceContext`, so a vector asserting
//     that `credential_id` is not exposed is asserting the projection production uses;
//   - per-device scoping comes from `WorkflowExecutor.ScopeOutputsToDevice`;
//   - the run namespace comes from `WorkflowExecutor.BuildRunContext`, so "null, never absent"
//     is a claim about the real field set.
//
// Those three were private statics on the orchestrator and are now internal. That is the only
// production change this adapter needed, and it changes no behaviour — an adapter that
// reimplemented any of them would be a second copy agreeing with itself, which is the failure
// this kit exists to remove.
//
// The condition form NEVER passes a device view. §9 is explicit that `{{ device.* }}` does not
// resolve in a condition, because an edge fires once at DAG level after its source completes
// and there is no single "current device" at that point. In this product that rule lives at the
// call site, which passes `deviceContext: null`.
public sealed partial class FlowWeaverAdapter
{
    private const string Unresolved = "unresolved";

    private static JsonElement? Templates(JsonElement input)
    {
        if (input.TryGetProperty("condition", out var condition))
            return Condition(input, condition.GetString() ?? string.Empty);

        if (!input.TryGetProperty("template", out var template))
            throw new InvalidOperationException("a templates vector needs either `template` or `condition`");

        var outputs = StepOutputs(PropOrNull(input, "outputs"));
        var device = PropOrNull(input, "device");
        var deviceView = device is { } d ? WorkflowExecutor.BuildDeviceContext(DeviceRow(d)) : (JsonElement?)null;

        var mode = Str(input, "consumer_target_mode") ?? "once";
        if (string.Equals(mode, "per_device", StringComparison.Ordinal) && device is { } scoped)
            outputs = WorkflowExecutor.ScopeOutputsToDevice(outputs, DeviceId(scoped));

        var resolver = new VariableResolver(NullLogger<VariableResolver>.Instance);
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["v"] = template });
        var resolved = resolver
            .Resolve(payload, outputs, deviceView, PropOrNull(input, "input"), RunJson(input, "run"))
            .GetProperty("v");

        return VariableResolver.FindUnresolvedTemplates(resolved).Count > 0
            ? JsonSerializer.SerializeToElement(new { resolved = Unresolved })
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["resolved"] = resolved });
    }

    private static JsonElement Condition(JsonElement input, string condition)
    {
        var ctx = PropOrNull(input, "context") ?? default;
        var evaluator = new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance);
        var result = evaluator.Evaluate(
            condition,
            StepOutputs(PropOrNull(ctx, "outputs")),
            deviceContext: null,
            runInput: PropOrNull(ctx, "input"),
            runContext: RunJson(ctx, "run"));
        return JsonSerializer.SerializeToElement(new { result });
    }

    // ── shared plumbing, also used by the executor family when it arrives ────

    internal static IReadOnlyDictionary<string, StepResult> StepOutputs(JsonElement? outputs)
    {
        var map = new Dictionary<string, StepResult>(StringComparer.Ordinal);
        if (outputs is not { ValueKind: JsonValueKind.Object } o) return map;
        foreach (var p in o.EnumerateObject()) map[p.Name] = new StepResult(p.Value.Clone());
        return map;
    }

    internal static JsonElement? RunJson(JsonElement holder, string property)
    {
        if (PropOrNull(holder, property) is not { ValueKind: JsonValueKind.Object } run) return null;

        return WorkflowExecutor.BuildRunContext(
            GuidOf(run, "id"),
            GuidOf(run, "workflow_id"),
            Str(run, "workflow_name") ?? string.Empty,
            Str(run, "environment") ?? string.Empty,
            Str(run, "trigger") ?? string.Empty,
            // Round-tripped so the vector's value and the projection's agree on format without
            // the vector needing a redaction rule for it.
            Str(run, "started_at") is { } at
                ? DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToString("o")
                : null,
            Str(run, "owner_email"),
            Str(run, "url"));
    }

    private static flow_weaver_backend.Models.Device DeviceRow(JsonElement d) => new()
    {
        DeviceId = DeviceId(d),
        DeviceName = Str(d, "name") ?? string.Empty,
        IpAddress = Str(d, "ip") ?? string.Empty,
        Platform = Str(d, "platform") ?? string.Empty,
        Vendor = Str(d, "vendor") ?? string.Empty,
        OsVersion = Str(d, "os_version") ?? string.Empty,
        Site = Str(d, "site") ?? string.Empty,
        Role = Str(d, "role") ?? string.Empty,
        Status = Str(d, "status") ?? string.Empty,
        ExternalId = Str(d, "external_id"),
        Properties = PropOrNull(d, "properties") ?? JsonDocument.Parse("{}").RootElement.Clone(),
        // Present on the row and absent from the view: that asymmetry is the point of §5, and
        // a vector asserts it by asking for `{{ device.credential_id }}` and expecting nothing.
        CredentialId = Str(d, "credential_id") is { } c && Guid.TryParse(c, out var cid) ? cid : Guid.Empty,
    };

    private static Guid DeviceId(JsonElement d) => GuidOf(d, "id");

    private static Guid GuidOf(JsonElement el, string prop) =>
        Str(el, prop) is { } s && Guid.TryParse(s, out var g) ? g : Guid.Empty;

    internal static JsonElement? PropOrNull(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : null;

    internal static JsonElement? PropOrNull(JsonElement? el, string name) =>
        el is { } e ? PropOrNull(e, name) : null;
}
