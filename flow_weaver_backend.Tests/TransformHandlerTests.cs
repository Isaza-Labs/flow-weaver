using System.Text.Json;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Traceability: FR-011 (TC-FW-011) — data-transformation semantics of the
// `transform` task type (distinct from PythonHandlerSandboxTests, which cover
// sandbox SECURITY, not processing). TransformHandler is a pure JMESPath
// evaluator over the input payload — no sandbox, DB, or network — so its
// contract is exercised directly.
public class TransformHandlerTests
{
    private static TransformHandler Handler() => new(NullLogger<TransformHandler>.Instance);

    private static SnippetRequest Req(string inputJson, string? snippetCode = null, string? scriptLanguage = null) => new()
    {
        StepRunId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        NodeId = "n",
        SnippetId = null,
        SnippetType = "transform",
        InputPayload = JsonDocument.Parse(inputJson).RootElement,
        DeviceId = null,
        SnippetCode = snippetCode,
        ScriptLanguage = scriptLanguage,
    };

    [Fact]
    public async Task Filter_projection_over_whole_payload()
    {
        var req = Req(
            """{"items":[{"active":true,"n":1},{"active":false,"n":2},{"active":true,"n":3}]}""",
            snippetCode: "items[?active].n");

        var result = await Handler().ExecuteAsync(req, default);

        Assert.True(result.Success);
        Assert.Equal(JsonValueKind.Array, result.Output.ValueKind);
        Assert.Equal(new[] { 1, 3 }, result.Output.EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public async Task Explicit_input_property_is_the_document_root()
    {
        // When the payload carries an `input` object, that becomes the doc.
        var req = Req("""{"input":{"a":42}}""", snippetCode: "a");

        var result = await Handler().ExecuteAsync(req, default);

        Assert.True(result.Success);
        Assert.Equal(42, result.Output.GetInt32());
    }

    [Fact]
    public async Task SnippetCode_expression_takes_precedence_over_inline_expression()
    {
        // SnippetCode ("b") wins over the inline input.expression ("a").
        var req = Req("""{"expression":"a","input":{"a":1,"b":2}}""", snippetCode: "b");

        var result = await Handler().ExecuteAsync(req, default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Output.GetInt32());
    }

    [Fact]
    public async Task Unsupported_language_fails_cleanly()
    {
        // ScriptLanguage wins over everything; only jmespath is supported.
        var req = Req("""{"x":1}""", snippetCode: "x", scriptLanguage: "python");

        var result = await Handler().ExecuteAsync(req, default);

        Assert.False(result.Success);
        Assert.Contains("unsupported", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_expression_fails_without_throwing()
    {
        var req = Req("""{"x":1}""", snippetCode: "[[[ not valid jmespath");

        var result = await Handler().ExecuteAsync(req, default);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error);
    }
}
