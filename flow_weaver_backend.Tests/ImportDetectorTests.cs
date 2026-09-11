using System.Text.Json;
using flow_weaver_backend.Services.Import.Detectors;

namespace flow_weaver_backend.Tests;

// S15: format detection unit tests. Cheap, no DB.
public class ImportDetectorTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void FlowWeaverV1Detector_recognises_native_export()
    {
        var doc = Parse("""
            {
              "schema_version": "v1",
              "name": "test",
              "nodes": [{ "id": "n1", "snippet_id": "ssh", "x": 0, "y": 0, "type": "task", "config_overrides": {} }],
              "edges": []
            }
            """);
        var score = new FlowWeaverV1Detector().Detect(doc);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void FlowWeaverV1Detector_returns_zero_for_other_shape()
    {
        var doc = Parse("""{ "tasks": {}, "transitions": {} }""");
        Assert.Equal(0.0, new FlowWeaverV1Detector().Detect(doc));
    }

    [Fact]
    public void N8nDetector_recognises_namespaced_node_type()
    {
        var doc = Parse("""
            {
              "name": "my n8n",
              "nodes": [{ "name": "HTTP Request", "type": "n8n-nodes-base.httpRequest", "parameters": {} }],
              "connections": {}
            }
            """);
        var score = new N8nDetector().Detect(doc);
        Assert.True(score >= 0.95);
    }

    [Fact]
    public void N8nDetector_handles_loose_match_without_namespace()
    {
        var doc = Parse("""
            {
              "nodes": [{ "name": "x", "type": "custom" }],
              "connections": { "x": { "main": [[]] } }
            }
            """);
        var score = new N8nDetector().Detect(doc);
        Assert.True(score > 0 && score < 0.95);
    }

    [Fact]
    public void ItentialDetector_recognises_tasks_with_app()
    {
        var doc = Parse("""
            {
              "name": "iap workflow",
              "tasks": {
                "t1": { "app": "@itential/adapter-http", "command": "get" }
              },
              "transitions": {}
            }
            """);
        var score = new ItentialDetector().Detect(doc);
        Assert.True(score >= 0.95);
    }

    [Fact]
    public void GenericDagDetector_is_low_baseline()
    {
        var doc = Parse("""
            {
              "nodes": [{ "id": "a" }],
              "edges": [{ "source": "a", "target": "b" }]
            }
            """);
        var score = new GenericDagDetector().Detect(doc);
        Assert.True(score > 0 && score < 0.5);
    }
}
