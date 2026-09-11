using System.Text.Json;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F4: the mcp_call workflow node handler. Reads config from the (already
// resolved) InputPayload and delegates to the shared McpToolExecutor.
public class McpCallHandlerTests
{
    private static SnippetRequest Req(object configOverrides) => new()
    {
        StepRunId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        NodeId = "n1",
        SnippetId = Guid.NewGuid(),
        SnippetType = "mcp_call",
        InputPayload = JsonSerializer.SerializeToElement(configOverrides),
        DeviceId = null,
    };

    [Fact]
    public void Type_and_idempotency()
    {
        var h = new McpCallHandler(new FakeExec(), NullLogger<McpCallHandler>.Instance);
        Assert.Equal("mcp_call", h.Type);
        Assert.Equal(IdempotencyKind.RequiresCompensation, h.DefaultIdempotency);
    }

    [Fact]
    public async Task Success_maps_to_snippet_result()
    {
        var server = Guid.NewGuid();
        var exec = new FakeExec { Result = new McpCallResult("hi", null, false) };
        var h = new McpCallHandler(exec, NullLogger<McpCallHandler>.Instance);

        var res = await h.ExecuteAsync(
            Req(new { mcp_server_id = server.ToString(), tool_name = "echo", arguments = new { q = 1 } }), default);

        Assert.True(res.Success);
        Assert.Equal(server, exec.LastServer);
        Assert.Equal("echo", exec.LastTool);
        Assert.Equal("hi", res.Output.GetProperty("content").GetString());
        Assert.False(res.Output.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task Tool_error_fails_the_step()
    {
        var exec = new FakeExec { Result = new McpCallResult("boom", null, true) };
        var h = new McpCallHandler(exec, NullLogger<McpCallHandler>.Instance);

        var res = await h.ExecuteAsync(Req(new { mcp_server_id = Guid.NewGuid().ToString(), tool_name = "x" }), default);

        Assert.False(res.Success);
        Assert.Contains("boom", res.Error);
    }

    [Fact]
    public async Task Missing_server_id_fails_without_dispatch()
    {
        var exec = new FakeExec();
        var h = new McpCallHandler(exec, NullLogger<McpCallHandler>.Instance);

        var res = await h.ExecuteAsync(Req(new { tool_name = "x" }), default);

        Assert.False(res.Success);
        Assert.Contains("mcp_server_id", res.Error);
        Assert.Null(exec.LastTool);
    }

    [Fact]
    public async Task Bad_server_id_fails()
    {
        var h = new McpCallHandler(new FakeExec(), NullLogger<McpCallHandler>.Instance);
        var res = await h.ExecuteAsync(Req(new { mcp_server_id = "not-a-guid", tool_name = "x" }), default);
        Assert.False(res.Success);
        Assert.Contains("GUID", res.Error);
    }

    [Fact]
    public async Task Missing_tool_name_fails()
    {
        var h = new McpCallHandler(new FakeExec(), NullLogger<McpCallHandler>.Instance);
        var res = await h.ExecuteAsync(Req(new { mcp_server_id = Guid.NewGuid().ToString() }), default);
        Assert.False(res.Success);
        Assert.Contains("tool_name", res.Error);
    }
}

file sealed class FakeExec : IMcpToolExecutor
{
    public McpCallResult Result = new("ok", null, false);
    public Guid LastServer;
    public string? LastTool;

    public Task<McpCallResult> ExecuteAsync(
        Guid mcpServerId, string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        LastServer = mcpServerId;
        LastTool = toolName;
        return Task.FromResult(Result);
    }
}
