using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Phase 2 of the RBAC-granular refactor (plan_rbac_granular.md): the resolver
// that turns a user's PermissionGrants into HasAsync/CapabilitiesAsync
// decisions — admin-bypass, default-deny, and ABAC condition matching.
public class EffectivePermissionsTests
{
    private static readonly Guid User = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static EffectivePermissions Resolver(AppDbContext db, params string[] roles) =>
        new(new FakeUser { UserId = User, Roles = roles },
            new PermissionGrantReader(db));

    private static async Task SeedBuiltinMembership(AppDbContext db, string role)
        => await new BuiltinGrantSync(db, NullLogger<BuiltinGrantSync>.Instance)
            .SyncUserAsync(User, role);

    private static void AddCustomGrant(AppDbContext db, string[] caps, string conditionsJson)
    {
        db.PermissionGrants.Add(new PermissionGrant
        {
            PermissionGrantId = Guid.NewGuid(),
            Name = "custom",
            Enabled = true,
            IsActive = true,
            SubjectIds = new() { User },
            Capabilities = caps.ToList(),
            Conditions = JsonDocument.Parse(conditionsJson).RootElement.Clone(),
        });
        db.SaveChanges();
    }

    // ── MCP dimensions (mcp_server / mcp_tool) ────────────────────────────

    [Fact]
    public async Task Mcp_tool_scoped_grant_matches_only_that_tool()
    {
        using var db = NewDb(nameof(Mcp_tool_scoped_grant_matches_only_that_tool));
        AddCustomGrant(db, new[] { "mcp.execute" }, """{ "mcp_tool": ["search"] }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("mcp.execute", new PermissionContext(McpToolName: "search")));
        Assert.False(await eff.HasAsync("mcp.execute", new PermissionContext(McpToolName: "delete")));
        Assert.False(await eff.HasAsync("mcp.execute"));                     // no leak into unscoped
        Assert.Contains("mcp.execute", await eff.CapabilitiesAsync());       // coarse set ignores conditions
    }

    [Fact]
    public async Task Mcp_server_scoped_grant_matches_only_that_server()
    {
        using var db = NewDb(nameof(Mcp_server_scoped_grant_matches_only_that_server));
        var srv = Guid.NewGuid();
        AddCustomGrant(db, new[] { "mcp.execute" }, $$"""{ "mcp_server": ["{{srv}}"] }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("mcp.execute", new PermissionContext(McpServerId: srv)));
        Assert.False(await eff.HasAsync("mcp.execute", new PermissionContext(McpServerId: Guid.NewGuid())));
        Assert.False(await eff.HasAsync("mcp.execute"));
    }

    [Fact]
    public async Task Mcp_server_and_tool_scoped_grant_requires_both()
    {
        using var db = NewDb(nameof(Mcp_server_and_tool_scoped_grant_requires_both));
        var srv = Guid.NewGuid();
        AddCustomGrant(db, new[] { "mcp.execute" }, $$"""{ "mcp_server": ["{{srv}}"], "mcp_tool": ["search"] }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("mcp.execute", new PermissionContext(McpServerId: srv, McpToolName: "search")));
        Assert.False(await eff.HasAsync("mcp.execute", new PermissionContext(McpServerId: srv, McpToolName: "other")));
        Assert.False(await eff.HasAsync("mcp.execute", new PermissionContext(McpServerId: Guid.NewGuid(), McpToolName: "search")));
    }

    [Fact]
    public async Task Mcp_tool_condition_is_case_sensitive()
    {
        // MCP tool names are case-sensitive; a grant for read_file must NOT
        // authorize a distinct READ_FILE tool.
        using var db = NewDb(nameof(Mcp_tool_condition_is_case_sensitive));
        AddCustomGrant(db, new[] { "mcp.execute" }, """{ "mcp_tool": ["read_file"] }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("mcp.execute", new PermissionContext(McpToolName: "read_file")));
        Assert.False(await eff.HasAsync("mcp.execute", new PermissionContext(McpToolName: "READ_FILE")));
    }

    // ── admin bypass ──────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_bypasses_everything_without_any_grant()
    {
        using var db = NewDb(nameof(Admin_bypasses_everything_without_any_grant));
        var eff = Resolver(db, "admin");

        Assert.True(await eff.HasAsync("secret.manage"));
        Assert.True(await eff.HasAsync("workflow.run",
            new PermissionContext(Environment: "production")));
        Assert.True(await eff.HasAsync("anything.not.in.catalogue"));

        var caps = await eff.CapabilitiesAsync();
        Assert.Equal(CapabilityCatalog.All.Count, caps.Count);
    }

    // ── built-in bundles reproduce the legacy tiers ───────────────────────

    [Fact]
    public async Task Viewer_member_holds_exactly_the_viewer_bundle()
    {
        using var db = NewDb(nameof(Viewer_member_holds_exactly_the_viewer_bundle));
        await SeedBuiltinMembership(db, "viewer");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("workflow.read"));
        Assert.False(await eff.HasAsync("workflow.update"));
        Assert.False(await eff.HasAsync("workflow.delete"));

        var caps = await eff.CapabilitiesAsync();
        Assert.True(caps.SetEquals(CapabilityCatalog.CapabilitiesForLegacyRole("viewer")));
    }

    [Fact]
    public async Task Operator_member_holds_the_operator_bundle_unconditionally()
    {
        using var db = NewDb(nameof(Operator_member_holds_the_operator_bundle_unconditionally));
        await SeedBuiltinMembership(db, "operator");
        var eff = Resolver(db, "operator");

        // Built-in bundles carry empty conditions → match every context.
        Assert.True(await eff.HasAsync("workflow.run"));
        Assert.True(await eff.HasAsync("workflow.run",
            new PermissionContext(Environment: "production",
                DevicePoolNames: new[] { "core" })));
        Assert.False(await eff.HasAsync("secret.manage"));

        var caps = await eff.CapabilitiesAsync();
        Assert.True(caps.SetEquals(CapabilityCatalog.CapabilitiesForLegacyRole("operator")));
    }

    [Fact]
    public async Task No_grants_denies_everything_with_empty_capability_set()
    {
        using var db = NewDb(nameof(No_grants_denies_everything_with_empty_capability_set));
        var eff = Resolver(db, "viewer");

        Assert.False(await eff.HasAsync("workflow.read"));
        Assert.Empty(await eff.CapabilitiesAsync());
    }

    // ── ABAC conditions ───────────────────────────────────────────────────

    [Fact]
    public async Task Env_scoped_grant_matches_only_that_environment()
    {
        using var db = NewDb(nameof(Env_scoped_grant_matches_only_that_environment));
        AddCustomGrant(db, new[] { "workflow.run" }, """{ "environment": ["qa"] }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("workflow.run", new PermissionContext(Environment: "qa")));
        Assert.False(await eff.HasAsync("workflow.run", new PermissionContext(Environment: "production")));
        // Scoped grant must not leak into an unscoped (global) check.
        Assert.False(await eff.HasAsync("workflow.run"));
        // Coarse capability set ignores conditions.
        Assert.Contains("workflow.run", await eff.CapabilitiesAsync());
    }

    [Fact]
    public async Task Device_pool_scoped_grant_matches_only_that_pool()
    {
        using var db = NewDb(nameof(Device_pool_scoped_grant_matches_only_that_pool));
        AddCustomGrant(db, new[] { "device.exec.write" }, """{ "device_pool": ["access-switches"] }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("device.exec.write",
            new PermissionContext(DevicePoolNames: new[] { "access-switches" })));
        Assert.False(await eff.HasAsync("device.exec.write",
            new PermissionContext(DevicePoolNames: new[] { "core-routers" })));
        Assert.False(await eff.HasAsync("device.exec.write"));
    }

    [Fact]
    public async Task Resource_scoped_grant_matches_only_that_resource()
    {
        using var db = NewDb(nameof(Resource_scoped_grant_matches_only_that_resource));
        var wf = Guid.NewGuid();
        var other = Guid.NewGuid();
        AddCustomGrant(db, new[] { "workflow.update" },
            $$"""{ "resource": { "type": "workflow", "id": "{{wf}}" } }""");
        var eff = Resolver(db, "viewer");

        Assert.True(await eff.HasAsync("workflow.update", PermissionContext.ForResource("workflow", wf)));
        Assert.False(await eff.HasAsync("workflow.update", PermissionContext.ForResource("workflow", other)));
        Assert.False(await eff.HasAsync("workflow.update", PermissionContext.ForResource("integration", wf)));
        Assert.False(await eff.HasAsync("workflow.update"));
    }

    [Fact]
    public async Task Multiple_conditions_are_and_joined()
    {
        using var db = NewDb(nameof(Multiple_conditions_are_and_joined));
        AddCustomGrant(db, new[] { "workflow.run" },
            """{ "environment": ["qa"], "device_pool": ["access-switches"] }""");
        var eff = Resolver(db, "viewer");

        // Both must be satisfied.
        Assert.True(await eff.HasAsync("workflow.run",
            new PermissionContext(Environment: "qa", DevicePoolNames: new[] { "access-switches" })));
        // env matches, pool doesn't.
        Assert.False(await eff.HasAsync("workflow.run",
            new PermissionContext(Environment: "qa", DevicePoolNames: new[] { "core" })));
        // pool matches, env missing.
        Assert.False(await eff.HasAsync("workflow.run",
            new PermissionContext(DevicePoolNames: new[] { "access-switches" })));
    }

    // ── guard rails ───────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_caller_is_denied()
    {
        using var db = NewDb(nameof(Unauthenticated_caller_is_denied));
        var eff = new EffectivePermissions(new AnonymousUser(), new PermissionGrantReader(db));

        Assert.False(await eff.HasAsync("workflow.read"));
        Assert.Empty(await eff.CapabilitiesAsync());
    }

    // ── transport ceiling (messaging channel) ─────────────────────────────

    [Fact]
    public async Task Transport_ceiling_caps_admin_too()
    {
        using var db = NewDb(nameof(Transport_ceiling_caps_admin_too));
        var eff = new EffectivePermissions(
            new FakeUser
            {
                UserId = User,
                Roles = new[] { "admin" },
                CapabilityCeiling = new[] { "workflow.read", "run.read" },
            },
            new PermissionGrantReader(db));

        Assert.True(await eff.HasAsync("workflow.read"));
        Assert.False(await eff.HasAsync("secret.manage"));   // admin, but outside the ceiling
        Assert.True((await eff.CapabilitiesAsync()).SetEquals(new[] { "workflow.read", "run.read" }));
    }

    [Fact]
    public async Task Transport_ceiling_intersects_a_users_grants()
    {
        using var db = NewDb(nameof(Transport_ceiling_intersects_a_users_grants));
        await SeedBuiltinMembership(db, "operator");   // full operator bundle
        var eff = new EffectivePermissions(
            new FakeUser
            {
                UserId = User,
                Roles = new[] { "operator" },
                CapabilityCeiling = new[] { "workflow.read" },
            },
            new PermissionGrantReader(db));

        Assert.True(await eff.HasAsync("workflow.read"));
        Assert.False(await eff.HasAsync("workflow.update"));   // operator holds it, ceiling excludes it
        Assert.True((await eff.CapabilitiesAsync()).SetEquals(new[] { "workflow.read" }));
    }

    private sealed class AnonymousUser : ICurrentUser
    {
        public Guid UserId => throw new InvalidOperationException("anonymous");
        public string? Username => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string>? CapabilityCeiling => null;
    }
}
