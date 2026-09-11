using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F1: McpServerService — CRUD with encrypted auth (secrets never echoed),
// and test/sync tool-cache upsert + reap.
public class McpServerServiceTests
{
    private static readonly FakeUser Caller = new();

    private static McpServerService NewService(AppDbContext db, IMcpClient client)
        => new(new McpServerRepository(db), new McpToolRepository(db), client,
               new FakeCrypto(), Caller, new FakeAudit(), NullLogger<McpServerService>.Instance);

    private static JsonElement Schema() => JsonSerializer.SerializeToElement(new { type = "object" });

    private static T Body<T>(ActionResult<T> r) where T : class
        => r.Value ?? (T)((ObjectResult)r.Result!).Value!;

    private static async Task<Guid> Create(McpServerService svc, McpAuthInput? auth = null, string authType = "none")
    {
        var res = await svc.CreateAsync(new CreateMcpServerRequest
        {
            Name = "srv", Url = "https://mcp.example.com/rpc", AuthType = authType, Auth = auth,
        });
        var created = Assert.IsType<CreatedAtActionResult>(res.Result);
        return ((McpServerResponse)created.Value!).McpServerId;
    }

    [Fact]
    public async Task Create_stores_encrypted_auth_and_never_echoes_secret()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient());

        var res = await svc.CreateAsync(new CreateMcpServerRequest
        {
            Name = "srv", Url = "https://x/mcp", AuthType = "bearer", Auth = new McpAuthInput { Token = "s3cret" },
        });
        var body = (McpServerResponse)Assert.IsType<CreatedAtActionResult>(res.Result).Value!;

        Assert.True(body.HasAuth);
        Assert.True(body.HasToken);
        Assert.False(body.HasApiKey);

        // The stored blob round-trips back to the token (encrypted at rest).
        var row = db.McpServers.Single();
        Assert.NotNull(row.AuthConfigEncrypted);
        var back = McpAuthConfigCodec.Decrypt(row.AuthConfigEncrypted, new FakeCrypto());
        Assert.Equal("s3cret", back.Token);
    }

    [Fact]
    public async Task Create_rejects_blank_name_and_bad_auth_type()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient());

        Assert.IsType<BadRequestObjectResult>(
            (await svc.CreateAsync(new CreateMcpServerRequest { Name = "  ", Url = "https://x" })).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await svc.CreateAsync(new CreateMcpServerRequest { Name = "s", Url = "https://x", AuthType = "weird" })).Result);
    }

    [Fact]
    public async Task Get_missing_returns_not_found()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient());
        Assert.IsType<NotFoundObjectResult>((await svc.GetAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Update_preserves_secret_when_auth_omitted()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient());
        var id = await Create(svc, new McpAuthInput { Token = "keep" }, "bearer");

        await svc.UpdateAsync(id, new UpdateMcpServerRequest { Name = "renamed" });

        var row = db.McpServers.Single();
        Assert.Equal("renamed", row.Name);
        Assert.Equal("keep", McpAuthConfigCodec.Decrypt(row.AuthConfigEncrypted, new FakeCrypto()).Token);
    }

    [Fact]
    public async Task Update_replaces_secret_when_auth_provided()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient());
        var id = await Create(svc, new McpAuthInput { Token = "old" }, "bearer");

        await svc.UpdateAsync(id, new UpdateMcpServerRequest { Auth = new McpAuthInput { Token = "new" } });

        Assert.Equal("new", McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, new FakeCrypto()).Token);
    }

    [Fact]
    public async Task Update_merges_auth_preserving_unsupplied_secrets()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient());
        var res = await svc.CreateAsync(new CreateMcpServerRequest
        {
            Name = "s", Url = "https://x", AuthType = "oauth_authorization_code",
            Auth = new McpAuthInput { ClientId = "cid", ClientSecret = "sec" },
        });
        var id = ((McpServerResponse)Assert.IsType<CreatedAtActionResult>(res.Result).Value!).McpServerId;

        // Editing only a non-secret field must not drop the client_secret.
        await svc.UpdateAsync(id, new UpdateMcpServerRequest { Auth = new McpAuthInput { TokenEndpoint = "https://as/token" } });

        var back = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, new FakeCrypto());
        Assert.Equal("sec", back.ClientSecret);
        Assert.Equal("cid", back.ClientId);
        Assert.Equal("https://as/token", back.TokenEndpoint);
    }

    [Fact]
    public async Task Delete_soft_deletes_server_and_tools()
    {
        using var db = TestDb.NewContext();
        var client = new SyncStubClient { Tools = { Tool("a"), Tool("b") } };
        var svc = NewService(db, client);
        var id = await Create(svc);
        await svc.TestAndSyncAsync(id);

        await svc.DeleteAsync(id);

        Assert.False(db.McpServers.Single().IsActive);
        Assert.All(db.McpTools, t => Assert.False(t.IsActive));
    }

    [Fact]
    public async Task Sync_upserts_tools_and_sets_ok()
    {
        using var db = TestDb.NewContext();
        var client = new SyncStubClient { Tools = { Tool("search"), Tool("fetch") } };
        var svc = NewService(db, client);
        var id = await Create(svc);

        var res = await svc.TestAndSyncAsync(id);
        var sync = Body(res);

        Assert.Equal("ok", sync.Status);
        Assert.Equal(2, sync.ToolsSynced);
        Assert.Equal(2, db.McpTools.Count(t => t.IsActive));
        var server = db.McpServers.Single();
        Assert.Equal("ok", server.Status);
        Assert.NotNull(server.LastToolsSyncedAt);
    }

    [Fact]
    public async Task Sync_reaps_tools_that_vanished()
    {
        using var db = TestDb.NewContext();
        var client = new SyncStubClient { Tools = { Tool("a"), Tool("b") } };
        var svc = NewService(db, client);
        var id = await Create(svc);
        await svc.TestAndSyncAsync(id);

        client.Tools = new() { Tool("a") };   // "b" disappeared
        await svc.TestAndSyncAsync(id);

        Assert.Equal(1, db.McpTools.Count(t => t.IsActive));
        Assert.Contains(db.McpTools, t => t.Name == "b" && !t.IsActive);
    }

    [Fact]
    public async Task Sync_failure_sets_unreachable()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient { Throw = new HttpRequestException("boom") });
        var id = await Create(svc);

        var sync = Body(await svc.TestAndSyncAsync(id));
        Assert.Equal("unreachable", sync.Status);
        Assert.NotNull(sync.Error);
        Assert.Equal("unreachable", db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Sync_401_sets_needs_authorization()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient { Throw = new InvalidOperationException("HTTP 401 Unauthorized") });
        var id = await Create(svc);

        Assert.Equal("needs_authorization", Body(await svc.TestAndSyncAsync(id)).Status);
    }

    [Fact]
    public async Task Sync_403_host_not_allowed_is_unreachable_not_auth()
    {
        using var db = TestDb.NewContext();
        // A DNS-rebinding / Host-allowlist rejection from the remote MCP server:
        // reachable, but NOT a credential problem. Must not read as needs_authorization,
        // or the operator is sent to re-enter a token that was never the issue.
        var svc = NewService(db, new SyncStubClient
        {
            Throw = new HttpRequestException(
                "Response status code does not indicate success: 403 (Forbidden). "
                + "Response body: forbidden: host not allowed"),
        });
        var id = await Create(svc);

        var sync = Body(await svc.TestAndSyncAsync(id));
        Assert.Equal("unreachable", sync.Status);
        Assert.Contains("host not allowed", sync.Error);
        Assert.Equal("unreachable", db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Sync_403_with_credential_hint_sets_needs_authorization()
    {
        using var db = TestDb.NewContext();
        var svc = NewService(db, new SyncStubClient { Throw = new HttpRequestException("403 Forbidden: invalid api key") });
        var id = await Create(svc);

        Assert.Equal("needs_authorization", Body(await svc.TestAndSyncAsync(id)).Status);
    }

    [Fact]
    public async Task List_returns_servers_with_tool_counts()
    {
        using var db = TestDb.NewContext();
        var client = new SyncStubClient { Tools = { Tool("a"), Tool("b"), Tool("c") } };
        var svc = NewService(db, client);
        var id = await Create(svc);
        await svc.TestAndSyncAsync(id);

        var list = Body(await svc.ListAsync(50, 0));
        Assert.Equal(1, list.Total);
        Assert.Equal(3, list.Data.Single().ToolCount);
    }

    private static McpToolDescriptor Tool(string name) => new(name, name, "desc", Schema());
}

file sealed class SyncStubClient : IMcpClient
{
    public List<McpToolDescriptor> Tools { get; set; } = new();
    public Exception? Throw;

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpServer server, CancellationToken ct = default)
    {
        if (Throw is not null) throw Throw;
        return Task.FromResult<IReadOnlyList<McpToolDescriptor>>(Tools);
    }

    public Task<McpCallResult> CallToolAsync(
        McpServer server, string toolName, JsonElement arguments, CancellationToken ct = default)
        => Task.FromResult(new McpCallResult(string.Empty, null, false));
}
