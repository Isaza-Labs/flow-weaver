using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F3: the three MCP agent tools — list_mcp_servers, discover_mcp_tools,
// call_mcp_tool (incl. its per-server/tool RBAC gate in granular mode).
public class McpAgentToolsTests
{
    private static readonly FakeUser Caller = new();

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);
    private static JsonElement Empty => JsonSerializer.SerializeToElement(new { });
    private static JsonElement Schema() => JsonSerializer.SerializeToElement(new { type = "object" });

    private static Guid Seed(AppDbContext db, params string[] toolNames)
    {
        var id = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = id, Name = "srv",
            Url = "https://x", Enabled = true, IsActive = true, Status = "ok",
        });
        foreach (var n in toolNames)
        {
            db.McpTools.Add(new McpTool
            {
                McpToolId = Guid.NewGuid(), McpServerId = id,
                Name = n, Description = n + " tool", InputSchema = Schema(), IsActive = true, Enabled = true,
            });
        }
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task List_mcp_servers_returns_servers_with_tool_counts()
    {
        using var db = TestDb.NewContext();
        Seed(db, "a", "b");
        var handler = new ListMcpServersHandler(new McpServerRepository(db), new McpToolRepository(db), Caller);

        var res = await handler.ExecuteAsync(Empty, CancellationToken.None);

        Assert.Equal(1, res.GetProperty("count").GetInt32());
        Assert.Equal(2, res.GetProperty("servers")[0].GetProperty("tool_count").GetInt32());
    }

    [Fact]
    public async Task Discover_mcp_tools_filters_by_keyword()
    {
        using var db = TestDb.NewContext();
        Seed(db, "search", "delete");
        var handler = new DiscoverMcpToolsHandler(new McpServerRepository(db), new McpToolRepository(db), Caller);

        var all = await handler.ExecuteAsync(Empty, CancellationToken.None);
        Assert.Equal(2, all.GetProperty("count").GetInt32());

        var filtered = await handler.ExecuteAsync(Args(new { keyword = "sear" }), CancellationToken.None);
        Assert.Equal(1, filtered.GetProperty("count").GetInt32());
        Assert.Equal("search", filtered.GetProperty("tools")[0].GetProperty("tool_name").GetString());
    }

    [Fact]
    public async Task Call_mcp_tool_delegates_to_executor_in_legacy_mode()
    {
        using var db = TestDb.NewContext();
        var id = Seed(db, "echo");
        var executor = new StubExecutor { Result = new McpCallResult("hello", null, false) };
        var handler = NewCallHandler(db, executor, new StubEffective(), new FakeAppSettings());

        var res = await handler.ExecuteAsync(
            Args(new { mcp_server_id = id.ToString(), tool_name = "echo", arguments = new { q = 1 } }),
            CancellationToken.None);

        Assert.False(res.GetProperty("is_error").GetBoolean());
        Assert.Equal("hello", res.GetProperty("content").GetString());
        Assert.Equal(id, executor.LastServer);
        Assert.Equal("echo", executor.LastTool);
    }

    [Fact]
    public async Task Call_mcp_tool_missing_server_is_error()
    {
        using var db = TestDb.NewContext();
        Seed(db, "echo");
        var executor = new StubExecutor();
        var handler = NewCallHandler(db, executor, new StubEffective(), new FakeAppSettings());

        var res = await handler.ExecuteAsync(
            Args(new { mcp_server_id = Guid.NewGuid().ToString(), tool_name = "echo" }), CancellationToken.None);

        Assert.True(res.GetProperty("is_error").GetBoolean());
        Assert.Null(executor.LastTool);   // never dispatched
    }

    [Fact]
    public async Task Call_mcp_tool_denied_by_granular_grant()
    {
        using var db = TestDb.NewContext();
        var id = Seed(db, "echo");
        var executor = new StubExecutor();
        var handler = NewCallHandler(db, executor, new StubEffective { Allow = false }, new GranularSettings());

        var res = await handler.ExecuteAsync(
            Args(new { mcp_server_id = id.ToString(), tool_name = "echo" }), CancellationToken.None);

        Assert.True(res.GetProperty("is_error").GetBoolean());
        Assert.Contains("permission denied", res.GetProperty("error").GetString());
        Assert.Null(executor.LastTool);   // gate fired before dispatch
    }

    [Fact]
    public async Task Call_mcp_tool_allowed_by_granular_grant()
    {
        using var db = TestDb.NewContext();
        var id = Seed(db, "echo");
        var executor = new StubExecutor { Result = new McpCallResult("ok", null, false) };
        var handler = NewCallHandler(db, executor, new StubEffective { Allow = true }, new GranularSettings());

        var res = await handler.ExecuteAsync(
            Args(new { mcp_server_id = id.ToString(), tool_name = "echo" }), CancellationToken.None);

        Assert.False(res.GetProperty("is_error").GetBoolean());
        Assert.Equal("echo", executor.LastTool);
    }

    private static CallMcpToolHandler NewCallHandler(
        AppDbContext db, IMcpToolExecutor executor, IEffectivePermissions eff, IAppSettingsService settings)
        => new(new McpServerRepository(db), executor, eff, settings, Caller, NullLogger<CallMcpToolHandler>.Instance);
}

file sealed class StubExecutor : IMcpToolExecutor
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

file sealed class StubEffective : IEffectivePermissions
{
    public bool Allow = true;
    public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default) => Task.FromResult(Allow);
    public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(Allow);
    public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}

file sealed class GranularSettings : IAppSettingsService
{
    public Task<AppSettings> GetAsync(CancellationToken ct = default)
        => Task.FromResult(new AppSettings { RbacMode = "granular" });
    public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
        => Task.FromResult(updated);
}
