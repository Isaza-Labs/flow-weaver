using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F0: McpToolExecutor loads the server and delegates to IMcpClient,
// translating not-found / disabled / transport failures into IsError results.
public class McpToolExecutorTests
{
    private static AppDbContext NewContext(string name)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static (McpToolExecutor exec, Guid serverId) Setup(
        string name, IMcpClient client, bool enabled = true, bool seed = true)
    {
        var db = NewContext(name);
        var serverId = Guid.NewGuid();
        if (seed)
        {
            db.McpServers.Add(new McpServer
            {
                McpServerId = serverId,
                Name = "srv",
                Url = "https://mcp.example.com",
                Enabled = enabled,
                IsActive = true,
            });
            db.SaveChanges();
        }
        var exec = new McpToolExecutor(new McpServerRepository(db), client, NullLogger<McpToolExecutor>.Instance);
        return (exec, serverId);
    }

    [Fact]
    public async Task Missing_server_returns_error()
    {
        var (exec, _) = Setup(nameof(Missing_server_returns_error), new StubMcpClient(), seed: false);
        var res = await exec.ExecuteAsync(Guid.NewGuid(), "tool", default);
        Assert.True(res.IsError);
        Assert.Contains("not found", res.Content);
    }

    [Fact]
    public async Task Disabled_server_returns_error()
    {
        var (exec, id) = Setup(nameof(Disabled_server_returns_error), new StubMcpClient(), enabled: false);
        var res = await exec.ExecuteAsync(id, "tool", default);
        Assert.True(res.IsError);
        Assert.Contains("disabled", res.Content);
    }

    [Fact]
    public async Task Empty_tool_name_returns_error()
    {
        var (exec, id) = Setup(nameof(Empty_tool_name_returns_error), new StubMcpClient());
        var res = await exec.ExecuteAsync(id, "  ", default);
        Assert.True(res.IsError);
        Assert.Contains("tool_name", res.Content);
    }

    [Fact]
    public async Task Delegates_to_client_on_success()
    {
        var client = new StubMcpClient { Result = new McpCallResult("hello", null, false) };
        var (exec, id) = Setup(nameof(Delegates_to_client_on_success), client);
        var res = await exec.ExecuteAsync(id, "echo", JsonSerializer.SerializeToElement(new { q = 1 }));
        Assert.False(res.IsError);
        Assert.Equal("hello", res.Content);
        Assert.Equal("echo", client.LastTool);
    }

    [Fact]
    public async Task Generic_exception_becomes_error_result()
    {
        var client = new StubMcpClient { Throw = new HttpRequestException("boom") };
        var (exec, id) = Setup(nameof(Generic_exception_becomes_error_result), client);
        var res = await exec.ExecuteAsync(id, "echo", default);
        Assert.True(res.IsError);
        Assert.Contains("failed", res.Content);
    }

    [Fact]
    public async Task Internal_timeout_becomes_error_result()
    {
        // OCE while the caller's token is NOT cancelled ⇒ treated as a timeout.
        var client = new StubMcpClient { Throw = new OperationCanceledException() };
        var (exec, id) = Setup(nameof(Internal_timeout_becomes_error_result), client);
        var res = await exec.ExecuteAsync(id, "echo", default);
        Assert.True(res.IsError);
        Assert.Contains("timed out", res.Content);
    }
}

file sealed class StubMcpClient : IMcpClient
{
    public McpCallResult? Result;
    public Exception? Throw;
    public string? LastTool;

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpServer server, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<McpToolDescriptor>>(Array.Empty<McpToolDescriptor>());

    public Task<McpCallResult> CallToolAsync(
        McpServer server, string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        LastTool = toolName;
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result ?? new McpCallResult("ok", null, false));
    }
}
