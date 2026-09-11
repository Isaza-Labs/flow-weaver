using System.Reflection;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Services.Import;

namespace flow_weaver_backend.Tests;

// Guards the YAML→JSON conversion inside WorkflowImportPipeline.ParseUpload.
// The earlier implementation deserialised YAML into `object?`, which made
// YamlDotNet collapse every scalar to a string. Re-serialising to JSON then
// produced `"x":"50"` and the v1 JSON Schema (which wants `x` as a number)
// rejected the document on commit — even for round-trips of FlowWeaver's
// own export.
//
// We reach the static helper via reflection because it's deliberately
// private (no other caller has a use for it).
public class WorkflowImportYamlParserTests
{
    private static JsonElement Invoke(string yaml)
    {
        var method = typeof(WorkflowImportPipeline).GetMethod(
            "ParseUpload",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseUpload not found");
        var result = method.Invoke(null, new object[] { Encoding.UTF8.GetBytes(yaml) });
        return (JsonElement)result!;
    }

    [Fact]
    public void Bare_numeric_scalars_become_json_numbers()
    {
        var yaml = """
            nodes:
            - id: a
              snippet_id: __start__
              x: 50
              y: 250.5
            edges: []
            """;
        var doc = Invoke(yaml);
        var node = doc.GetProperty("nodes")[0];
        Assert.Equal(JsonValueKind.Number, node.GetProperty("x").ValueKind);
        Assert.Equal(50, node.GetProperty("x").GetInt32());
        Assert.Equal(JsonValueKind.Number, node.GetProperty("y").ValueKind);
        Assert.Equal(250.5, node.GetProperty("y").GetDouble());
    }

    [Fact]
    public void Quoted_numeric_scalars_stay_strings()
    {
        var yaml = """
            workflow:
              id: "12345"
              ratio: '0.5'
            """;
        var doc = Invoke(yaml);
        var wf = doc.GetProperty("workflow");
        Assert.Equal(JsonValueKind.String, wf.GetProperty("id").ValueKind);
        Assert.Equal("12345", wf.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.String, wf.GetProperty("ratio").ValueKind);
    }

    [Fact]
    public void Booleans_and_nulls_are_typed()
    {
        var yaml = """
            enabled: true
            disabled: false
            empty: null
            tilde: ~
            missing:
            """;
        var doc = Invoke(yaml);
        Assert.Equal(JsonValueKind.True, doc.GetProperty("enabled").ValueKind);
        Assert.Equal(JsonValueKind.False, doc.GetProperty("disabled").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("empty").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("tilde").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("missing").ValueKind);
    }

    [Fact]
    public void Guid_like_strings_stay_strings()
    {
        // Snippet ids must not be misinterpreted as numbers even though
        // they contain digits — they fail both Integer and Float regex
        // patterns so they fall through to the string branch.
        var yaml = """
            snippet_id: 8e5af2cc-1234-5678-9abc-def012345678
            """;
        var doc = Invoke(yaml);
        Assert.Equal(JsonValueKind.String, doc.GetProperty("snippet_id").ValueKind);
        Assert.Equal(
            "8e5af2cc-1234-5678-9abc-def012345678",
            doc.GetProperty("snippet_id").GetString());
    }

    [Fact]
    public void Sentinel_node_ids_round_trip()
    {
        var yaml = """
            nodes:
            - id: __start__
              snippet_id: __start__
              x: 0
              y: 0
              config_overrides: {}
            """;
        var doc = Invoke(yaml);
        var node = doc.GetProperty("nodes")[0];
        Assert.Equal("__start__", node.GetProperty("id").GetString());
        Assert.Equal("__start__", node.GetProperty("snippet_id").GetString());
        Assert.Equal(JsonValueKind.Object, node.GetProperty("config_overrides").ValueKind);
    }

    [Fact]
    public void Json_payload_passes_through_unchanged()
    {
        var json = """{"nodes":[{"id":"a","x":1}],"edges":[]}""";
        var doc = Invoke(json);
        Assert.Equal(JsonValueKind.Number, doc.GetProperty("nodes")[0].GetProperty("x").ValueKind);
    }
}
