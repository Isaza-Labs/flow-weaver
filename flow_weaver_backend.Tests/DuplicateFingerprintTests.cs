using System.Text.Json;
using flow_weaver_backend.Services.Import;

namespace flow_weaver_backend.Tests;

public class DuplicateFingerprintTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Identical_workflows_produce_identical_fingerprint()
    {
        var a = Parse("""
            {
              "nodes": [
                { "id": "a", "snippet_id": "ssh",  "config_overrides": { "command": "show version" } },
                { "id": "b", "snippet_id": "ping", "config_overrides": {} }
              ],
              "edges": [{ "source": "a", "target": "b", "type": "success" }]
            }
            """);
        var b = Parse("""
            {
              "nodes": [
                { "id": "x", "snippet_id": "ssh",  "config_overrides": { "command": "show version" } },
                { "id": "y", "snippet_id": "ping", "config_overrides": {} }
              ],
              "edges": [{ "source": "x", "target": "y", "type": "success" }]
            }
            """);
        Assert.Equal(DuplicateFingerprint.Compute(a), DuplicateFingerprint.Compute(b));
    }

    [Fact]
    public void Different_snippet_types_produce_different_fingerprints()
    {
        var a = Parse("""
            {
              "nodes": [{ "id": "a", "snippet_id": "ssh", "config_overrides": {} }],
              "edges": []
            }
            """);
        var b = Parse("""
            {
              "nodes": [{ "id": "a", "snippet_id": "ping", "config_overrides": {} }],
              "edges": []
            }
            """);
        Assert.NotEqual(DuplicateFingerprint.Compute(a), DuplicateFingerprint.Compute(b));
    }

    [Fact]
    public void Object_key_order_is_normalised()
    {
        var a = Parse("""
            {
              "nodes": [{ "id": "a", "snippet_id": "rest_call", "config_overrides": { "x": 1, "y": 2 } }],
              "edges": []
            }
            """);
        var b = Parse("""
            {
              "nodes": [{ "snippet_id": "rest_call", "id": "a", "config_overrides": { "y": 2, "x": 1 } }],
              "edges": []
            }
            """);
        Assert.Equal(DuplicateFingerprint.Compute(a), DuplicateFingerprint.Compute(b));
    }

    [Fact]
    public void Similarity_is_1_for_exact_match()
    {
        var fp = "sha256:abcdef";
        Assert.Equal(1.0, DuplicateFingerprint.Similarity(fp, fp));
    }
}
