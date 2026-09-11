using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Covers the template substitution contract used by WorkflowExecutor when
// building step inputs. Each test exercises one grammar or resolution rule
// in isolation so a failure pinpoints the exact broken behavior.
public class VariableResolverTests
{
    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    private static VariableResolver NewResolver() =>
        new VariableResolver(NullLogger<VariableResolver>.Instance);

    private static Dictionary<string, StepResult> Steps(params (string id, string json)[] entries)
    {
        var dict = new Dictionary<string, StepResult>();
        foreach (var (id, json) in entries)
            dict[id] = new StepResult(E(json));
        return dict;
    }

    [Fact]
    public void Whole_string_template_preserves_type()
    {
        var resolver = NewResolver();
        var payload = E("""{ "count": "{{ steps.ping.output.success_count }}" }""");
        var steps = Steps(("ping", """{ "success_count": 7 }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal(JsonValueKind.Number, result.GetProperty("count").ValueKind);
        Assert.Equal(7, result.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Inline_template_stringifies_value()
    {
        var resolver = NewResolver();
        var payload = E("""{ "msg": "rtt={{ steps.ping.output.rtt }}ms" }""");
        var steps = Steps(("ping", """{ "rtt": 12 }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("rtt=12ms", result.GetProperty("msg").GetString());
    }

    [Fact]
    public void Hyphenated_node_id_resolves()
    {
        // Regression: the pre-fix regex used \w+ which rejected hyphens and
        // silently left every hyphenated reference as literal text.
        var resolver = NewResolver();
        var payload = E("""{ "out": "{{ steps.ssh-lldp.output.stdout }}" }""");
        var steps = Steps(("ssh-lldp", """{ "stdout": "hello" }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("hello", result.GetProperty("out").GetString());
    }

    [Fact]
    public void Array_index_in_path_resolves()
    {
        var resolver = NewResolver();
        var payload = E("""{ "iface": "{{ steps.sync.output.updated[0].interface }}" }""");
        var steps = Steps(("sync",
            """{ "updated": [ {"interface": "Ethernet1"}, {"interface": "Ethernet2"} ] }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("Ethernet1", result.GetProperty("iface").GetString());
    }

    [Fact]
    public void Array_index_out_of_range_keeps_literal()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.sync.output.updated[5].interface }}" }""");
        var steps = Steps(("sync",
            """{ "updated": [ {"interface": "Ethernet1"} ] }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("{{ steps.sync.output.updated[5].interface }}",
            result.GetProperty("x").GetString());
    }

    [Fact]
    public void Whole_string_object_reference_preserves_shape()
    {
        var resolver = NewResolver();
        var payload = E("""{ "dev": "{{ steps.sync.output.devices[0] }}" }""");
        var steps = Steps(("sync",
            """{ "devices": [ {"id": "a", "ok": true} ] }"""));

        var result = resolver.Resolve(payload, steps);

        var dev = result.GetProperty("dev");
        Assert.Equal(JsonValueKind.Object, dev.ValueKind);
        Assert.Equal("a", dev.GetProperty("id").GetString());
        Assert.True(dev.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Bare_output_reference_returns_whole_output()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.ping.output }}" }""");
        var steps = Steps(("ping", """{ "rtt": 12, "ok": true }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal(12, result.GetProperty("x").GetProperty("rtt").GetInt32());
    }

    [Fact]
    public void Missing_step_keeps_literal()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.missing.output.foo }}" }""");
        var steps = Steps();

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("{{ steps.missing.output.foo }}",
            result.GetProperty("x").GetString());
    }

    [Fact]
    public void Missing_path_segment_keeps_literal()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.ping.output.not_there }}" }""");
        var steps = Steps(("ping", """{ "rtt": 12 }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("{{ steps.ping.output.not_there }}",
            result.GetProperty("x").GetString());
    }

    [Fact]
    public void Nested_objects_and_arrays_are_walked()
    {
        var resolver = NewResolver();
        var payload = E("""
            {
              "outer": {
                "items": [
                  { "value": "{{ steps.a.output.x }}" },
                  { "value": "prefix-{{ steps.a.output.x }}" }
                ]
              }
            }
            """);
        var steps = Steps(("a", """{ "x": 42 }"""));

        var result = resolver.Resolve(payload, steps);

        var items = result.GetProperty("outer").GetProperty("items");
        Assert.Equal(42, items[0].GetProperty("value").GetInt32());
        Assert.Equal("prefix-42", items[1].GetProperty("value").GetString());
    }

    [Fact]
    public void Per_device_envelope_path_resolves_via_index()
    {
        // Simulates the fan-out aggregate shape written by
        // WorkflowExecutor.AggregatePerDeviceOutput. Without array indexing
        // support, downstream `once`-mode nodes could not reach per-device
        // outputs at all.
        var resolver = NewResolver();
        var payload = E("""{ "stdout": "{{ steps.ssh.output.devices[1].output.stdout }}" }""");
        var steps = Steps(("ssh", """
            {
              "devices": [
                { "device_id": "a", "status": "completed", "output": { "stdout": "A" } },
                { "device_id": "b", "status": "completed", "output": { "stdout": "B" } }
              ]
            }
            """));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("B", result.GetProperty("stdout").GetString());
    }

    [Fact]
    public void Devices_by_name_resolves_by_hostname()
    {
        // Phase-1 ergonomic: `devices_by_name` exposes the same per-device
        // entries keyed by hostname so authors don't have to know `[N]`.
        var resolver = NewResolver();
        var payload = E("""{ "stdout": "{{ steps.ssh.output.devices_by_name['router-2'].output.stdout }}" }""");
        var steps = Steps(("ssh", """
            {
              "devices": [
                { "device": "router-1", "device_name": "router-1", "hostname": "router-1",
                  "status": "completed", "output": { "stdout": "A" } },
                { "device": "router-2", "device_name": "router-2", "hostname": "router-2",
                  "status": "completed", "output": { "stdout": "B" } }
              ],
              "devices_by_name": {
                "router-1": { "device": "router-1", "output": { "stdout": "A" } },
                "router-2": { "device": "router-2", "output": { "stdout": "B" } }
              }
            }
            """));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("B", result.GetProperty("stdout").GetString());
    }

    [Fact]
    public void First_success_resolves_when_present()
    {
        var resolver = NewResolver();
        var payload = E("""{ "name": "{{ steps.ssh.output.first_success.device_name }}" }""");
        var steps = Steps(("ssh", """
            {
              "devices": [
                { "device": "r1", "device_name": "r1", "status": "failed", "error": "x" },
                { "device": "r2", "device_name": "r2", "status": "completed", "output": {} }
              ],
              "first_success": { "device": "r2", "device_name": "r2", "status": "completed", "output": {} }
            }
            """));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("r2", result.GetProperty("name").GetString());
    }

    [Fact]
    public void First_success_null_keeps_literal()
    {
        // When every device failed, `first_success` is null and templates
        // referencing its sub-paths should leave the literal in place
        // (existing missing-path semantics).
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.ssh.output.first_success.device_name }}" }""");
        var steps = Steps(("ssh", """{ "first_success": null }"""));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("{{ steps.ssh.output.first_success.device_name }}",
            result.GetProperty("x").GetString());
    }

    [Fact]
    public void Devices_array_entry_exposes_device_name_and_hostname_aliases()
    {
        // The actual envelope written by AggregatePerDeviceOutput now
        // includes both aliases. This guards the contract templates rely
        // on without depending on the executor itself.
        var resolver = NewResolver();
        var payload = E("""
            {
              "n": "{{ steps.ssh.output.devices[0].device_name }}",
              "h": "{{ steps.ssh.output.devices[0].hostname }}"
            }
            """);
        var steps = Steps(("ssh", """
            {
              "devices": [
                { "device": "router-1", "device_name": "router-1", "hostname": "router-1",
                  "status": "completed", "output": {} }
              ]
            }
            """));

        var result = resolver.Resolve(payload, steps);

        Assert.Equal("router-1", result.GetProperty("n").GetString());
        Assert.Equal("router-1", result.GetProperty("h").GetString());
    }

    // ── device.* templates ────────────────────────────────────────────

    private static JsonElement DeviceCtx(string json) => E(json);

    [Fact]
    public void Device_property_resolves()
    {
        var resolver = NewResolver();
        var payload = E("""{ "host": "{{ device.ip }}" }""");
        var deviceCtx = DeviceCtx("""{ "name": "rtr-1", "ip": "10.0.0.5" }""");

        var result = resolver.Resolve(payload, Steps(), deviceCtx);

        Assert.Equal("10.0.0.5", result.GetProperty("host").GetString());
    }

    [Fact]
    public void Device_inline_template_stringifies()
    {
        var resolver = NewResolver();
        var payload = E("""{ "msg": "running on {{ device.name }} ({{ device.ip }})" }""");
        var deviceCtx = DeviceCtx("""{ "name": "rtr-1", "ip": "10.0.0.5" }""");

        var result = resolver.Resolve(payload, Steps(), deviceCtx);

        Assert.Equal("running on rtr-1 (10.0.0.5)", result.GetProperty("msg").GetString());
    }

    [Fact]
    public void Bare_device_reference_returns_whole_context()
    {
        var resolver = NewResolver();
        var payload = E("""{ "dev": "{{ device }}" }""");
        var deviceCtx = DeviceCtx("""{ "name": "rtr-1", "ip": "10.0.0.5", "role": "core" }""");

        var result = resolver.Resolve(payload, Steps(), deviceCtx);

        var dev = result.GetProperty("dev");
        Assert.Equal(JsonValueKind.Object, dev.ValueKind);
        Assert.Equal("rtr-1", dev.GetProperty("name").GetString());
        Assert.Equal("core", dev.GetProperty("role").GetString());
    }

    [Fact]
    public void Device_nested_path_with_array_index_resolves()
    {
        var resolver = NewResolver();
        var payload = E("""{ "tag": "{{ device.properties.tags[1] }}" }""");
        var deviceCtx = DeviceCtx("""{ "properties": { "tags": ["edge", "qa", "vpn"] } }""");

        var result = resolver.Resolve(payload, Steps(), deviceCtx);

        Assert.Equal("qa", result.GetProperty("tag").GetString());
    }

    [Fact]
    public void Device_template_without_context_keeps_literal()
    {
        // `once`-mode steps with multiple targets have no single device
        // context — the resolver must leave the template intact rather
        // than silently rendering the empty string or crashing.
        var resolver = NewResolver();
        var payload = E("""{ "host": "{{ device.ip }}" }""");

        var result = resolver.Resolve(payload, Steps(), deviceContext: null);

        Assert.Equal("{{ device.ip }}", result.GetProperty("host").GetString());
    }

    [Fact]
    public void Device_word_boundary_rejects_lookalike_identifiers()
    {
        // Regression guard: a naive regex (\{\{\s*device[^}]*\}\}) would
        // happily match `{{ deviceName }}` / `{{ devices[0] }}` and
        // produce surprising substitutions. The real regex anchors
        // `device` on a word boundary.
        var resolver = NewResolver();
        var payload = E("""{ "a": "{{ deviceName }}", "b": "{{ devices[0] }}" }""");
        var deviceCtx = DeviceCtx("""{ "name": "rtr-1" }""");

        var result = resolver.Resolve(payload, Steps(), deviceCtx);

        Assert.Equal("{{ deviceName }}", result.GetProperty("a").GetString());
        Assert.Equal("{{ devices[0] }}", result.GetProperty("b").GetString());
    }

    [Fact]
    public void Device_and_step_templates_coexist_in_same_string()
    {
        var resolver = NewResolver();
        var payload = E("""{ "line": "{{ device.name }}: exit={{ steps.ssh.output.exit_code }}" }""");
        var deviceCtx = DeviceCtx("""{ "name": "rtr-1" }""");
        var steps = Steps(("ssh", """{ "exit_code": 0 }"""));

        var result = resolver.Resolve(payload, steps, deviceCtx);

        Assert.Equal("rtr-1: exit=0", result.GetProperty("line").GetString());
    }

    [Fact]
    public void Device_missing_path_keeps_literal()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ device.nope }}" }""");
        var deviceCtx = DeviceCtx("""{ "name": "rtr-1" }""");

        var result = resolver.Resolve(payload, Steps(), deviceCtx);

        Assert.Equal("{{ device.nope }}", result.GetProperty("x").GetString());
    }

    // ── filter pipes ──────────────────────────────────────────────────

    [Fact]
    public void Filter_trim_removes_surrounding_whitespace()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.t | trim }}" }""");
        var steps = Steps(("s", """{ "t": "  hello  " }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("hello", result.GetProperty("x").GetString());
    }

    [Fact]
    public void Filter_default_fires_when_path_missing()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.absent | default('-') }}" }""");
        var steps = Steps(("s", """{ "t": "x" }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("-", result.GetProperty("x").GetString());
    }

    [Fact]
    public void Filter_default_passes_through_when_value_present()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.t | default('-') }}" }""");
        var steps = Steps(("s", """{ "t": "real" }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("real", result.GetProperty("x").GetString());
    }

    [Fact]
    public void Filter_truncate_clips_to_length()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.t | truncate(5) }}" }""");
        var steps = Steps(("s", """{ "t": "abcdefghij" }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("abcde", result.GetProperty("x").GetString());
    }

    [Fact]
    public void Filter_strip_ansi_cleans_terminal_codes()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.t | strip_ansi }}" }""");
        var steps = Steps(("s", """{ "t": "\u001B[0mclean\u001B[0m" }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("clean", result.GetProperty("x").GetString());
    }

    [Fact]
    public void Filter_chain_applies_in_order()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.t | strip_ansi | trim }}" }""");
        var steps = Steps(("s", """{ "t": "  \u001B[0mhello\u001B[0m  " }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("hello", result.GetProperty("x").GetString());
    }

    // ── string-key bracket access ─────────────────────────────────────

    [Fact]
    public void String_key_bracket_access_resolves_object_property()
    {
        // devices_by_name uses hostnames as keys; hostnames may contain
        // hyphens, dots, or numerics that aren't valid C# identifiers.
        // Quoted bracket access lets templates name them without resorting
        // to numeric indices into devices[].
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.map['router-1'].name }}" }""");
        var steps = Steps(("s", """{ "map": { "router-1": { "name": "edge" } } }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("edge", result.GetProperty("x").GetString());
    }

    [Fact]
    public void String_key_bracket_access_with_double_quotes_also_works()
    {
        var resolver = NewResolver();
        var payload = E("""{ "x": "{{ steps.s.output.map[\"k\"] }}" }""");
        var steps = Steps(("s", """{ "map": { "k": "v" } }"""));
        var result = resolver.Resolve(payload, steps);
        Assert.Equal("v", result.GetProperty("x").GetString());
    }
}
