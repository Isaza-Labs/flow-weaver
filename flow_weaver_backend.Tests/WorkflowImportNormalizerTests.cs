using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Guards the strict-schema sanitizer that WorkflowImportController runs
// just before _schemaValidator.Validate. The validator uses
// `additionalProperties: false`, which means any stray key the exporter
// or a foreign translator emits crashes the import. The normalizer
// keeps only the fields the v1 schema actually accepts and drops
// out-of-enum `type` values + null-valued optional edge fields.
public class WorkflowImportNormalizerTests
{
    private static JsonElement Normalize(string method, string json)
    {
        var info = typeof(WorkflowImportController).GetMethod(
            method,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{method} not found");
        using var doc = JsonDocument.Parse(json);
        return (JsonElement)info.Invoke(null, new object[] { doc.RootElement })!;
    }

    private static JsonElement NormNodes(string json) => Normalize("NormalizeNodesForV1Schema", json);
    private static JsonElement NormEdges(string json) => Normalize("NormalizeEdgesForV1Schema", json);

    [Fact]
    public void Nodes_drop_out_of_enum_type()
    {
        // Sentinel start/end nodes from older saves carried `type:"start"`.
        // The schema enum is task/decision/subflow only — drop it so the
        // optional default ("task") applies.
        var json = """
            [
              { "id": "__start__", "snippet_id": "__start__", "x": 0, "y": 0, "type": "start" },
              { "id": "step",      "snippet_id": "abc",       "x": 1, "y": 2, "type": "task" }
            ]
            """;
        var result = NormNodes(json);
        Assert.False(result[0].TryGetProperty("type", out _));
        Assert.True(result[1].TryGetProperty("type", out var taskType));
        Assert.Equal("task", taskType.GetString());
    }

    [Fact]
    public void Nodes_strip_unknown_keys()
    {
        var json = """
            [{ "id": "a", "snippet_id": "b", "x": 1, "y": 2, "label": "hi", "data": {}, "width": 200 }]
            """;
        var result = NormNodes(json);
        var keys = result[0].EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("id", keys);
        Assert.Contains("snippet_id", keys);
        Assert.Contains("x", keys);
        Assert.Contains("y", keys);
        Assert.DoesNotContain("label", keys);
        Assert.DoesNotContain("data", keys);
        Assert.DoesNotContain("width", keys);
    }

    [Fact]
    public void Nodes_keep_config_overrides_as_object()
    {
        var json = """[{ "id": "a", "snippet_id": "b", "x": 1, "y": 2, "config_overrides": { "k": 1 } }]""";
        var result = NormNodes(json);
        var co = result[0].GetProperty("config_overrides");
        Assert.Equal(JsonValueKind.Object, co.ValueKind);
        Assert.Equal(1, co.GetProperty("k").GetInt32());
    }

    [Fact]
    public void Nodes_default_config_overrides_when_missing_or_null()
    {
        var json = """[{ "id": "a", "snippet_id": "b", "x": 1, "y": 2, "config_overrides": null }]""";
        var result = NormNodes(json);
        Assert.Equal(JsonValueKind.Object, result[0].GetProperty("config_overrides").ValueKind);
        Assert.Empty(result[0].GetProperty("config_overrides").EnumerateObject());
    }

    [Fact]
    public void Edges_drop_null_condition()
    {
        // YAML round-trips of edges without a condition tend to come back
        // with `condition: null`. The schema demands a string, so drop it.
        var json = """
            [
              { "source": "a", "target": "b", "type": "success", "condition": null, "source_handle": null, "target_handle": null }
            ]
            """;
        var result = NormEdges(json);
        Assert.False(result[0].TryGetProperty("condition", out _));
        Assert.False(result[0].TryGetProperty("source_handle", out _));
        Assert.False(result[0].TryGetProperty("target_handle", out _));
        Assert.Equal("success", result[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Edges_preserve_non_empty_condition()
    {
        var json = """
            [{ "source": "a", "target": "b", "type": "conditional", "condition": "$.result == 'ok'" }]
            """;
        var result = NormEdges(json);
        Assert.Equal("$.result == 'ok'", result[0].GetProperty("condition").GetString());
    }

    [Fact]
    public void Edges_drop_out_of_enum_type()
    {
        // Edge without a type field is invalid per the schema (`type` is
        // required). The normalizer drops the bad value; the schema
        // validator will then surface the missing-type error cleanly
        // instead of the misleading "wrong enum value".
        var json = """[{ "source": "a", "target": "b", "type": "compensation" }]""";
        var result = NormEdges(json);
        Assert.False(result[0].TryGetProperty("type", out _));
    }

    // Regression: JsonSchema.Net reports each anyOf arm's failure even
    // when a different arm passes (so the overall validation succeeds).
    // The previous flattener walked every sub-evaluation and produced
    // phantom "Value should match one of the values specified by the
    // enum" lines for every Guid string. The walker now stops at valid
    // parents so only genuine failures reach the user.
    [Fact]
    public void Schema_validator_does_not_surface_phantom_anyof_errors()
    {
        var validator = SharedSchemaValidator.Instance;
        var guid = Guid.NewGuid().ToString();
        using var nodes = JsonDocument.Parse($$"""
            [
              { "id": "__start__", "snippet_id": "__start__", "x": 0, "y": 0, "config_overrides": {} },
              { "id": "s1", "snippet_id": "{{guid}}", "x": 1, "y": 1, "config_overrides": {} },
              { "id": "__end__", "snippet_id": "__end__", "x": 2, "y": 2, "config_overrides": {} }
            ]
            """);
        using var edges = JsonDocument.Parse("""
            [
              { "source": "__start__", "target": "s1", "type": "success" },
              { "source": "s1", "target": "__end__", "type": "success" }
            ]
            """);
        var result = validator.Validate(nodes.RootElement, edges.RootElement);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}
