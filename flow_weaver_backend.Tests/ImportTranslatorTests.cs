using System.Text.Json;
using flow_weaver_backend.Services.Import.Translators;

namespace flow_weaver_backend.Tests;

// S15: deterministic translator coverage (n8n, Itential, v1 passthrough).
// AgentTranslator is excluded — it depends on an external LLM and is
// validated via the wizard's end-to-end smoke test.
public class ImportTranslatorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task FlowWeaverV1Translator_is_identity()
    {
        var doc = Parse("""
            {
              "schema_version": "v1",
              "name": "x",
              "nodes": [{ "id": "a", "snippet_id": "ssh", "x": 0, "y": 0, "type": "task", "config_overrides": {} }],
              "edges": []
            }
            """);
        var result = await new FlowWeaverV1Translator().TranslateAsync(doc, default);
        Assert.Equal(doc.GetRawText(), result.V1Workflow.GetRawText());
    }

    [Fact]
    public async Task N8nTranslator_maps_httpRequest_to_rest_call()
    {
        var doc = Parse("""
            {
              "name": "n8n",
              "nodes": [
                { "name": "Get user", "type": "n8n-nodes-base.httpRequest", "parameters": { "requestMethod": "GET", "url": "https://x/u" }, "position": [100, 100] }
              ],
              "connections": {}
            }
            """);
        var result = await new N8nTranslator().TranslateAsync(doc, default);
        var nodes = result.V1Workflow.GetProperty("nodes");
        // start + the one node + end
        Assert.Equal(3, nodes.GetArrayLength());
        var middle = nodes.EnumerateArray().Skip(1).First();
        Assert.Equal("rest_call", middle.GetProperty("snippet_id").GetString());
        Assert.Equal("GET", middle.GetProperty("config_overrides").GetProperty("method").GetString());
    }

    [Fact]
    public async Task N8nTranslator_wires_start_and_end()
    {
        var doc = Parse("""
            {
              "name": "n8n",
              "nodes": [{ "name": "step1", "type": "n8n-nodes-base.code", "parameters": {} }],
              "connections": {}
            }
            """);
        var result = await new N8nTranslator().TranslateAsync(doc, default);
        var edges = result.V1Workflow.GetProperty("edges");
        // start → step1, step1 → end
        Assert.True(edges.GetArrayLength() >= 2);
        var sources = edges.EnumerateArray().Select(e => e.GetProperty("source").GetString()).ToList();
        Assert.Contains("__start__", sources);
    }

    [Fact]
    public async Task ItentialTranslator_maps_http_app_to_rest_call()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "fetch": { "app": "@itential/adapter-http", "command": "GET", "parameters": { "url": "https://x" } }
              },
              "transitions": {}
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var nodes = result.V1Workflow.GetProperty("nodes");
        var middle = nodes.EnumerateArray().Skip(1).First();
        Assert.Equal("rest_call", middle.GetProperty("snippet_id").GetString());
    }

    [Fact]
    public async Task ItentialTranslator_maps_ssh_app_to_ssh()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "show": { "app": "@itential/adapter-ssh", "command": "show version" }
              },
              "transitions": {}
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var nodes = result.V1Workflow.GetProperty("nodes");
        var middle = nodes.EnumerateArray().Skip(1).First();
        Assert.Equal("ssh", middle.GetProperty("snippet_id").GetString());
    }

    [Fact]
    public async Task ItentialTranslator_treats_failure_alias_as_failure_edge()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "a": { "app": "@itential/adapter-ssh", "command": "ls" },
                "b": { "app": "@itential/adapter-ssh", "command": "pwd" }
              },
              "transitions": {
                "a": { "error": ["b"] }
              }
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var edges = result.V1Workflow.GetProperty("edges").EnumerateArray()
            .Where(e => e.GetProperty("source").GetString() == "a"
                && e.GetProperty("target").GetString() == "b")
            .ToList();
        Assert.NotEmpty(edges);
        Assert.Equal("failure", edges[0].GetProperty("type").GetString());
    }

    // Real Itential exports use `transitions[from][target] = { state }`
    // (target keyed, state in the value). The legacy translator parsed
    // that as if `target` were the state and produced a star pattern
    // pointing every node directly at `__end__` — losing the entire
    // DAG. Guard against that regression.
    [Fact]
    public async Task ItentialTranslator_parses_real_target_keyed_transitions()
    {
        var doc = Parse("""
            {
              "name": "real-itential",
              "tasks": {
                "workflow_start": { "name": "workflow_start", "nodeLocation": { "x": 0, "y": 0 } },
                "workflow_end":   { "name": "workflow_end",   "nodeLocation": { "x": 800, "y": 0 } },
                "a1f2": { "name": "getDevices",         "app": "Netbox",   "displayName": "Netbox" },
                "b3c4": { "name": "listAllHostRecords", "app": "Infoblox", "displayName": "Infoblox" }
              },
              "transitions": {
                "workflow_start": { "a1f2": { "type": "standard", "state": "success" } },
                "a1f2":           { "b3c4": { "type": "standard", "state": "success" } },
                "b3c4":           { "workflow_end": { "type": "standard", "state": "success" } }
              }
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var edges = result.V1Workflow.GetProperty("edges").EnumerateArray().ToList();

        // The chain start → a1f2 → b3c4 → end must survive intact.
        Assert.Contains(edges, e =>
            e.GetProperty("source").GetString() == "__start__"
            && e.GetProperty("target").GetString() == "a1f2");
        Assert.Contains(edges, e =>
            e.GetProperty("source").GetString() == "a1f2"
            && e.GetProperty("target").GetString() == "b3c4");
        Assert.Contains(edges, e =>
            e.GetProperty("source").GetString() == "b3c4"
            && e.GetProperty("target").GetString() == "__end__");

        // workflow_start / workflow_end must not become task nodes —
        // they map to FlowWeaver sentinels.
        var nodeIds = result.V1Workflow.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("id").GetString())
            .ToHashSet();
        Assert.DoesNotContain("workflow_start", nodeIds);
        Assert.DoesNotContain("workflow_end", nodeIds);
        Assert.Contains("__start__", nodeIds);
        Assert.Contains("__end__", nodeIds);
    }

    [Fact]
    public async Task ItentialTranslator_emits_failure_edge_from_state_failure_value()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "step":   { "name": "step",   "app": "@itential/adapter-ssh" },
                "cleanup":{ "name": "cleanup","app": "@itential/adapter-ssh" }
              },
              "transitions": {
                "step": { "cleanup": { "type": "standard", "state": "failure" } }
              }
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var edge = result.V1Workflow.GetProperty("edges").EnumerateArray()
            .Single(e => e.GetProperty("source").GetString() == "step"
                      && e.GetProperty("target").GetString() == "cleanup");
        Assert.Equal("failure", edge.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ItentialTranslator_reads_variables_incoming_and_translates_var_templates()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "fetch": {
                  "name": "getDevices",
                  "app": "Netbox",
                  "displayName": "Netbox",
                  "variables": {
                    "incoming": {
                      "site": "$var.job.formData.netboxSite",
                      "limit": 1000
                    }
                  }
                },
                "transform": {
                  "name": "transformation",
                  "app": "WorkFlowEngine",
                  "variables": {
                    "incoming": {
                      "variableMap": {
                        "netboxDevices": "$var.fetch.response"
                      }
                    }
                  }
                }
              },
              "transitions": {
                "fetch": { "transform": { "type": "standard", "state": "success" } }
              }
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var nodes = result.V1Workflow.GetProperty("nodes").EnumerateArray().ToList();

        // `fetch` is a non-WFE integration_action — its incoming
        // params land under config_overrides.params (the canonical
        // shape the runtime's IntegrationActionHandler reads).
        var fetch = nodes.Single(n => n.GetProperty("id").GetString() == "fetch");
        var fetchParams = fetch.GetProperty("config_overrides").GetProperty("params");
        Assert.Equal("{{ input.formData.netboxSite }}", fetchParams.GetProperty("site").GetString());
        Assert.Equal(1000, fetchParams.GetProperty("limit").GetInt32());

        // `transform` is a WFE transformation → mapped to a
        // python_snippet placeholder (Phase 1). python_snippets read
        // their inputs from the root of config_overrides; the
        // translated `$var.…` templates land there directly.
        var transform = nodes.Single(n => n.GetProperty("id").GetString() == "transform");
        Assert.StartsWith("python_transform_",
            transform.GetProperty("snippet_id").GetString() ?? "");
        var nested = transform.GetProperty("config_overrides").GetProperty("variableMap");
        Assert.Equal("{{ steps.fetch.output.response }}", nested.GetProperty("netboxDevices").GetString());
    }

    // Phase 1: WorkFlowEngine `evaluation` tasks are collapsed into
    // conditional edges anchored at the predecessor. The boolean
    // expression is reconstructed from `evaluation_groups`; `success`
    // keeps it, `failure` uses its negation. The evaluation node
    // itself is NOT emitted.
    [Fact]
    public async Task ItentialTranslator_collapses_evaluation_to_conditional_edges()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "transform": {
                  "name": "transformation", "app": "WorkFlowEngine", "task_name": "compareXY",
                  "variables": { "incoming": {}, "outgoing": {} }
                },
                "eval": {
                  "name": "evaluation", "app": "WorkFlowEngine",
                  "variables": { "incoming": {
                    "all_true_flag": false,
                    "evaluation_groups": [{
                      "all_true_flag": true,
                      "evaluations": [{
                        "operand_1": { "variable": "mismatchCount", "task": "transform" },
                        "operator": ">",
                        "operand_2": { "variable": "0", "task": "static" }
                      }]
                    }]
                  } }
                },
                "alert": { "name": "alert",  "app": "Tools",         "type": "operation" },
                "log":   { "name": "log",    "app": "Tools",         "type": "operation" }
              },
              "transitions": {
                "transform": { "eval":  { "type": "standard", "state": "success" } },
                "eval":      { "alert": { "type": "standard", "state": "success" },
                               "log":   { "type": "standard", "state": "failure" } }
              }
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);

        // `eval` is NOT in the nodes list.
        var nodeIds = result.V1Workflow.GetProperty("nodes")
            .EnumerateArray()
            .Select(n => n.GetProperty("id").GetString())
            .ToHashSet();
        Assert.DoesNotContain("eval", nodeIds);

        // `transform → alert` (conditional, success expression) and
        // `transform → log` (conditional, negated) both anchored at
        // the predecessor.
        var edges = result.V1Workflow.GetProperty("edges").EnumerateArray().ToList();
        var success = edges.Single(e =>
            e.GetProperty("source").GetString() == "transform"
            && e.GetProperty("target").GetString() == "alert");
        Assert.Equal("conditional", success.GetProperty("type").GetString());
        Assert.Equal("{{ steps.transform.output.mismatchCount }} > 0",
            success.GetProperty("condition").GetString());

        var failure = edges.Single(e =>
            e.GetProperty("source").GetString() == "transform"
            && e.GetProperty("target").GetString() == "log");
        Assert.Equal("conditional", failure.GetProperty("type").GetString());
        Assert.Equal("{{ steps.transform.output.mismatchCount }} <= 0",
            failure.GetProperty("condition").GetString());
    }

    [Fact]
    public async Task ItentialTranslator_leaves_complex_evaluation_as_python_snippet_placeholder()
    {
        // Two evaluations in one group → compound expression. The
        // current implementation only collapses single-eval groups
        // (negating compound expressions is not safe without `!`).
        // The node stays as a python_snippet placeholder for AI gen.
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "eval": {
                  "name": "evaluation", "app": "WorkFlowEngine",
                  "variables": { "incoming": {
                    "all_true_flag": true,
                    "evaluation_groups": [{
                      "all_true_flag": true,
                      "evaluations": [
                        { "operand_1": { "variable": "a", "task": "x" }, "operator": ">", "operand_2": { "variable": "0", "task": "static" } },
                        { "operand_1": { "variable": "b", "task": "x" }, "operator": "<", "operand_2": { "variable": "9", "task": "static" } }
                      ]
                    }]
                  } }
                }
              },
              "transitions": {}
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var nodes = result.V1Workflow.GetProperty("nodes").EnumerateArray().ToList();
        var eval = nodes.Single(n => n.GetProperty("id").GetString() == "eval");
        // Non-collapsed evaluations become python_snippet placeholders
        // (prefix `wfe_`) so the wizard's "Generate with AI" can draft
        // the body. The DAG keeps the node so the user can still see
        // there's a decision step that needs custom logic.
        var sid = eval.GetProperty("snippet_id").GetString() ?? "";
        Assert.StartsWith("python_wfe_", sid);
    }

    [Fact]
    public async Task ItentialTranslator_maps_wfe_transformation_to_python_snippet_placeholder()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "tr": {
                  "name": "transformation",
                  "app": "WorkFlowEngine",
                  "task_name": "compareNetboxInfobloxDeviceNames",
                  "description": "compares device name sets and produces matched/onlyInX",
                  "variables": { "incoming": {}, "outgoing": {} }
                }
              },
              "transitions": {}
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var node = result.V1Workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "tr");
        var sid = node.GetProperty("snippet_id").GetString() ?? "";
        Assert.StartsWith("python_transform_", sid);
        Assert.Contains("comparenetboxinfobloxdevicenames", sid);
    }

    [Fact]
    public async Task ItentialTranslator_maps_wfe_stub_to_notify_failure_placeholder()
    {
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "fail": {
                  "name": "stub", "app": "WorkFlowEngine",
                  "variables": { "incoming": { "type": "error" } }
                }
              },
              "transitions": {}
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var node = result.V1Workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "fail");
        var sid = node.GetProperty("snippet_id").GetString() ?? "";
        Assert.StartsWith("notify_failure_", sid);
        // The Itential stub's `incoming` data (`type: "error"`, …) is
        // useless for any FlowWeaver target — config_overrides must be
        // empty so map/generate targets apply their own defaults
        // without leftover junk.
        Assert.Equal(JsonValueKind.Object, node.GetProperty("config_overrides").ValueKind);
        Assert.Empty(node.GetProperty("config_overrides").EnumerateObject());
    }

    [Fact]
    public async Task ItentialTranslator_rejects_unsafe_string_literal_in_evaluation()
    {
        // A static operand whose value contains a backslash or template
        // braces can't be safely emitted as a quoted condition literal
        // — the translator falls back to leaving the evaluation
        // un-collapsed (python_snippet placeholder), letting the AI
        // step handle the comparison instead of producing a broken
        // condition string.
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "eval": {
                  "name": "evaluation", "app": "WorkFlowEngine",
                  "variables": { "incoming": {
                    "all_true_flag": true,
                    "evaluation_groups": [{
                      "all_true_flag": true,
                      "evaluations": [{
                        "operand_1": { "variable": "x", "task": "src" },
                        "operator": "==",
                        "operand_2": { "variable": "value\\ with backslash", "task": "static" }
                      }]
                    }]
                  } }
                }
              },
              "transitions": {}
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var eval = result.V1Workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "eval");
        // Eval was NOT collapsed — appears as a python_snippet
        // placeholder. The DAG keeps it as a real node.
        Assert.StartsWith("python_wfe_", eval.GetProperty("snippet_id").GetString() ?? "");
    }

    [Fact]
    public async Task ItentialTranslator_does_not_double_wire_internal_nodes_to_end()
    {
        // Regression: the old translator wired every non-source node to
        // __end__ as a "leaf cleanup" step, producing a star pattern
        // that overrode the real transitions.
        var doc = Parse("""
            {
              "name": "iap",
              "tasks": {
                "workflow_start": { "name": "workflow_start" },
                "workflow_end":   { "name": "workflow_end" },
                "a": { "name": "a", "app": "Netbox" },
                "b": { "name": "b", "app": "Netbox" }
              },
              "transitions": {
                "workflow_start": { "a": { "type": "standard", "state": "success" } },
                "a":              { "b": { "type": "standard", "state": "success" } },
                "b":              { "workflow_end": { "type": "standard", "state": "success" } }
              }
            }
            """);
        var result = await new ItentialTranslator().TranslateAsync(doc, default);
        var edges = result.V1Workflow.GetProperty("edges").EnumerateArray()
            .Where(e => e.GetProperty("target").GetString() == "__end__")
            .ToList();
        // Only `b` should connect to __end__ (via the real transition).
        // `a` already has an outgoing edge to `b`, so the orphan pass
        // must not add a duplicate `a → __end__`.
        Assert.Single(edges);
        Assert.Equal("b", edges[0].GetProperty("source").GetString());
    }
}
