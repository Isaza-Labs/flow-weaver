using System.Text.Json;
using flow_weaver_backend.Services.Mcp;
using ModelContextProtocol.Protocol;

namespace flow_weaver_backend.Tests;

// F0: McpClient.Normalize (CallToolResult → McpCallResult) and ToArguments
// (JsonElement → argument dict). Exercised directly (internal statics).
public class McpClientTests
{
    private static CallToolResult Result(bool? isError, JsonElement? structured, params string[] texts)
        => new()
        {
            Content = texts.Select(t => (ContentBlock)new TextContentBlock { Text = t }).ToList(),
            IsError = isError,
            StructuredContent = structured,
        };

    [Fact]
    public void Normalize_concatenates_text_blocks()
    {
        var res = McpClient.Normalize(Result(false, null, "a", "b"));
        Assert.Equal("a\nb", res.Content);
        Assert.False(res.IsError);
        Assert.Null(res.Structured);
    }

    [Fact]
    public void Normalize_reports_error_flag()
    {
        Assert.True(McpClient.Normalize(Result(true, null, "x")).IsError);
        // Null IsError defaults to false.
        Assert.False(McpClient.Normalize(Result(null, null, "x")).IsError);
    }

    [Fact]
    public void Normalize_carries_structured_content()
    {
        var structured = JsonSerializer.SerializeToElement(new { count = 3 });
        var res = McpClient.Normalize(Result(false, structured, "x"));
        Assert.NotNull(res.Structured);
        Assert.Equal(3, res.Structured!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Normalize_truncates_oversized_content()
    {
        var big = new string('a', 120_000);
        var res = McpClient.Normalize(Result(false, null, big));
        Assert.EndsWith("(truncated)", res.Content);
        Assert.True(res.Content.Length < big.Length);
    }

    [Fact]
    public void ToArguments_maps_object_properties()
    {
        var args = McpClient.ToArguments(JsonSerializer.SerializeToElement(new { a = 1, b = "x" }));
        Assert.NotNull(args);
        Assert.Equal(2, args!.Count);
        Assert.Equal(1, args["a"].GetInt32());
        Assert.Equal("x", args["b"].GetString());
    }

    [Fact]
    public void ToArguments_returns_null_for_non_object()
    {
        Assert.Null(McpClient.ToArguments(JsonSerializer.SerializeToElement(5)));
        Assert.Null(McpClient.ToArguments(JsonSerializer.SerializeToElement(new[] { 1, 2 })));
    }

    [Fact]
    public void ToArguments_empty_object_is_empty_dict()
    {
        var args = McpClient.ToArguments(JsonSerializer.SerializeToElement(new { }));
        Assert.NotNull(args);
        Assert.Empty(args!);
    }
}
