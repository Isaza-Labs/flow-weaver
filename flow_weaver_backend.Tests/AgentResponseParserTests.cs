using System.Text.Json;
using flow_weaver_backend.Services.Import.Translators;

namespace flow_weaver_backend.Tests;

// FU-4: AgentResponseParser is the pure-function piece of the
// AgentTranslator we can unit-test without standing up an LLM.
// Covers fence-stripping, wrapped-vs-bare workflow shape, and the
// fallback when the response isn't valid JSON.
public class AgentResponseParserTests
{
    [Fact]
    public void Parses_wrapped_response_with_notes_and_warnings()
    {
        var raw = """
            {
              "workflow": { "schema_version": "v1", "name": "x", "nodes": [], "edges": [] },
              "notes": ["Mapped n8n HTTP to rest_call", "Dropped retry config"],
              "warnings": ["Cycle detected; broke via fallback edge"]
            }
            """;
        var result = AgentResponseParser.Parse(raw);
        Assert.Equal("v1", result.V1Workflow.GetProperty("schema_version").GetString());
        Assert.Equal(2, result.Notes.Count);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Parses_bare_workflow_object()
    {
        var raw = """
            { "schema_version": "v1", "name": "y", "nodes": [], "edges": [] }
            """;
        var result = AgentResponseParser.Parse(raw);
        Assert.Equal("y", result.V1Workflow.GetProperty("name").GetString());
        // Bare-object branch surfaces a synthetic note explaining the lack of commentary.
        Assert.Contains("bare", result.Notes[0], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("```json\n{ \"workflow\": { \"name\": \"z\" } }\n```")]
    [InlineData("```\n{ \"workflow\": { \"name\": \"z\" } }\n```")]
    [InlineData("  ```json\n{ \"workflow\": { \"name\": \"z\" } }\n```  ")]
    public void Strips_markdown_fence(string fenced)
    {
        var result = AgentResponseParser.Parse(fenced);
        Assert.Equal("z", result.V1Workflow.GetProperty("name").GetString());
    }

    [Fact]
    public void Returns_empty_v1_fallback_when_response_is_not_json()
    {
        var raw = "Sorry, I cannot translate this workflow.";
        var result = AgentResponseParser.Parse(raw);
        // Fallback v1 carries the two sentinels and one start→end edge.
        Assert.Equal("v1", result.V1Workflow.GetProperty("schema_version").GetString());
        Assert.Equal(2, result.V1Workflow.GetProperty("nodes").GetArrayLength());
        Assert.Single(result.V1Workflow.GetProperty("edges").EnumerateArray());
        Assert.Contains("non-JSON", result.Notes[0]);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Filters_non_string_array_entries()
    {
        var raw = """
            {
              "workflow": { "schema_version": "v1", "name": "x", "nodes": [], "edges": [] },
              "notes": ["valid", 42, null, "also valid"],
              "warnings": []
            }
            """;
        var result = AgentResponseParser.Parse(raw);
        Assert.Equal(2, result.Notes.Count);
        Assert.Equal("valid", result.Notes[0]);
        Assert.Equal("also valid", result.Notes[1]);
    }

    [Fact]
    public void EmptyV1_is_a_valid_v1_shell()
    {
        var empty = AgentResponseParser.EmptyV1();
        Assert.Equal("v1", empty.GetProperty("schema_version").GetString());
        var nodes = empty.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        var ids = nodes.EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains("__start__", ids);
        Assert.Contains("__end__", ids);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("```\nplain\n```", "plain")]
    [InlineData("```yaml\nplain\n```", "plain")]
    [InlineData("  ```\nmulti\nline\n```  ", "multi\nline")]
    public void ExtractJsonBlock_trims_fences_correctly(string input, string expected)
    {
        Assert.Equal(expected, AgentResponseParser.ExtractJsonBlock(input));
    }
}
