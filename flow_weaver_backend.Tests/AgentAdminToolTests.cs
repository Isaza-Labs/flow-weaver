using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using DeviceModel = flow_weaver_backend.Models.Device;
using UserModel = flow_weaver_backend.Models.User;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The agent's inventory and administration tools.
//
// `set_user_role` is the one with real privilege implications: in granular
// mode a non-admin can hold `user.manage`, so without an extra check they
// could hand themselves the admin role through the chat. That guard is the
// centrepiece here.
public class AgentAdminToolTests
{

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    // ─── get_workflow_details ───────────────────────────────────────────

    private static GetWorkflowDetailsHandler Details(AppDbContext db)
        => new(new RepositoryBase<WorkflowModel>(db),
               new FakeUser(),
               TestAppLinks.Relative(),
               NullLogger<GetWorkflowDetailsHandler>.Instance);

    private static Guid SeedWorkflow(
        AppDbContext db, bool active = true, string nodes = "[]")
    {
        var id = Guid.NewGuid();
        db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = id,
            Name = "lldp-sync",
            Description = "collects neighbours",
            Environment = "draft",
            Version = 2,
            SchemaVersion = "v1",
            Nodes = TestJson.Element(nodes),
            Edges = TestJson.Element("[]"),
            Metadata = TestJson.Element("{}"),
            IsActive = active,
        });
        db.SaveChanges();
        return id;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"workflow_id":"not-a-guid"}""")]
    public async Task Details_ABadIdIsAnErrorPayload(string args)
    {
        using var db = TestDb.NewContext();

        var result = await Details(db).ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("workflow_id (uuid) is required", Error(result));
    }

    [Fact]
    public async Task Details_AnUnknownWorkflowIsReported()
    {
        using var db = TestDb.NewContext();

        var result = await Details(db).ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + Guid.NewGuid() + "\"}"), default);

        Assert.Equal("workflow not found", Error(result));
    }

    // The agent can only put a link in its answer if the tool hands it one —
    // it is explicitly told never to assemble a path from an id itself.
    [Fact]
    public async Task Details_CarryTheWorkflowPageLink()
    {
        using var db = TestDb.NewContext();
        var id = SeedWorkflow(db);

        var result = await Details(db).ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + id + "\"}"), default);

        Assert.Equal($"/workflows/{id}", result.GetProperty("url").GetString());
    }

    // With a public base url configured the link is absolute, so it survives
    // leaving the browser (an answer relayed to Telegram / Slack).
    [Fact]
    public async Task Details_LinkIsAbsoluteWhenAPublicBaseUrlIsConfigured()
    {
        using var db = TestDb.NewContext();
        var id = SeedWorkflow(db);
        var handler = new GetWorkflowDetailsHandler(
            new RepositoryBase<WorkflowModel>(db),
            new FakeUser(),
            // Trailing slash on purpose — the option is operator-typed.
            TestAppLinks.WithBase("https://flow.example.com/"),
            NullLogger<GetWorkflowDetailsHandler>.Instance);

        var result = await handler.ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + id + "\"}"), default);

        Assert.Equal($"https://flow.example.com/workflows/{id}", result.GetProperty("url").GetString());
    }

    // A soft-deleted workflow is still inspectable — the agent is routinely
    // asked about one after it was archived.
    [Fact]
    public async Task Details_ASoftDeletedWorkflowIsStillInspectable()
    {
        using var db = TestDb.NewContext();
        var id = SeedWorkflow(db, active: false);

        var result = await Details(db).ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + id + "\"}"), default);

        Assert.Null(Error(result));
    }

    // The whole graph is returned: this is the tool the agent uses to reason
    // about an existing workflow before editing it.
    [Fact]
    public async Task Details_ReturnsTheFullGraphAndMetadata()
    {
        using var db = TestDb.NewContext();
        var id = SeedWorkflow(db, nodes: """[{"id":"a","snippet_id":"__start__"}]""");

        var result = await Details(db).ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + id + "\"}"), default);

        Assert.Equal("lldp-sync", result.GetProperty("name").GetString());
        Assert.Equal(2, result.GetProperty("version").GetInt32());
        Assert.Equal("draft", result.GetProperty("environment").GetString());
        Assert.Equal("v1", result.GetProperty("schema_version").GetString());
        Assert.Equal("a", result.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("id").GetString());
    }

    // ─── query_devices ──────────────────────────────────────────────────

    private static QueryDevicesHandler Query(AppDbContext db)
        => new(new DeviceRepository(db),
               new FakeUser(),
               NullLogger<QueryDevicesHandler>.Instance);

    private static void SeedDevice(
        AppDbContext db, string name = "r1", string platform = "cisco_ios",
        string vendor = "cisco", string site = "madrid", string role = "core",
        string properties = "{}", bool active = true)
    {
        db.Devices.Add(new DeviceModel
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = name,
            IpAddress = "10.0.0.1",
            Platform = platform,
            Vendor = vendor,
            Site = site,
            Role = role,
            OsVersion = "17.9",
            Status = "active",
            Properties = TestJson.Element(properties),
            IsActive = active,
        });
        db.SaveChanges();
    }

    private static async Task<List<JsonElement>> RunQuery(AppDbContext db, string args)
        => (await Query(db).ExecuteAsync(TestJson.Element(args), default))
            .EnumerateArray().ToList();

    [Fact]
    public async Task Query_ReturnsTheActiveInventory()
    {
        using var db = TestDb.NewContext();
        SeedDevice(db, "r1");
        SeedDevice(db, "gone", active: false);

        var rows = await RunQuery(db, "{}");

        Assert.Equal("r1", Assert.Single(rows).GetProperty("DeviceName").GetString());
    }

    [Theory]
    [InlineData("""{"platform":"cisco_ios"}""", 1)]
    [InlineData("""{"platform":"junos"}""", 0)]
    [InlineData("""{"vendor":"cisco"}""", 1)]
    [InlineData("""{"site":"madrid"}""", 1)]
    [InlineData("""{"site":"barcelona"}""", 0)]
    [InlineData("""{"role":"core"}""", 1)]
    public async Task Query_EachFilterNarrowsTheResult(string args, int expected)
    {
        using var db = TestDb.NewContext();
        SeedDevice(db);

        Assert.Equal(expected, (await RunQuery(db, args)).Count);
    }

    // The limit is clamped so a bad model-supplied value can't pull the whole
    // inventory into the chat context.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task Query_TheLimitIsClamped(int requested, int expected)
    {
        using var db = TestDb.NewContext();
        SeedDevice(db, "r1");
        SeedDevice(db, "r2");

        Assert.Equal(expected, (await RunQuery(db, "{\"limit\":" + requested + "}")).Count);
    }

    // The hardware model lives in the device's custom Properties blob, and
    // different inventories spell the key differently — the agent needs it to
    // pick model-specific commands.
    [Theory]
    [InlineData("""{"model":"C9300"}""")]
    [InlineData("""{"device_model":"C9300"}""")]
    [InlineData("""{"part_number":"C9300"}""")]
    [InlineData("""{"hardware":"C9300"}""")]
    [InlineData("""{"MODEL":"C9300"}""")]
    public async Task Query_TheHardwareModelIsFoundUnderAnyOfItsCommonKeys(string properties)
    {
        using var db = TestDb.NewContext();
        SeedDevice(db, properties: properties);

        Assert.Equal("C9300", Assert.Single(await RunQuery(db, "{}")).GetProperty("Model").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"serial":"ABC"}""")]
    [InlineData("null")]
    public async Task Query_AnInventoryWithoutAModelFieldReportsNull(string properties)
    {
        using var db = TestDb.NewContext();
        SeedDevice(db, properties: properties);

        Assert.Equal(JsonValueKind.Null,
            Assert.Single(await RunQuery(db, "{}")).GetProperty("Model").ValueKind);
    }

    // The version/role/site trio is what lets the agent pick the right CLI
    // syntax, so it has to ride along.
    [Fact]
    public async Task Query_TheRowCarriesWhatTheAgentNeedsToPickCommands()
    {
        using var db = TestDb.NewContext();
        SeedDevice(db);

        var row = Assert.Single(await RunQuery(db, "{}"));
        Assert.Equal("cisco_ios", row.GetProperty("Platform").GetString());
        Assert.Equal("17.9", row.GetProperty("OsVersion").GetString());
        Assert.Equal("core", row.GetProperty("Role").GetString());
        Assert.Equal("madrid", row.GetProperty("Site").GetString());
    }

    // ─── set_user_role ──────────────────────────────────────────────────

    private sealed class RecordingGrantSync : flow_weaver_backend.Services.Permission.IBuiltinGrantSync
    {
        public List<(Guid UserId, string Role)> Synced { get; } = new();
        public Task EnsureGrantsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncUserAsync(Guid userId, string role, CancellationToken ct = default)
        {
            Synced.Add((userId, role));
            return Task.CompletedTask;
        }
    }

    private static (SetUserRoleHandler Handler, RecordingGrantSync Sync) SetRole(
        AppDbContext db, params string[] callerRoles)
    {
        var sync = new RecordingGrantSync();
        return (new SetUserRoleHandler(
            db,
            new FakeUser { Roles = callerRoles.Length == 0 ? new[] { "admin" } : callerRoles },
            sync,
            NullLogger<SetUserRoleHandler>.Instance), sync);
    }

    private static Guid SeedUser(
        AppDbContext db, string role = "viewer", bool active = true)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new UserModel
        {
            UserId = id,
            Username = "alice",
            Email = "alice@test",
            PasswordHash = "x",
            Role = role,
            IsActive = active,
        });
        db.SaveChanges();
        return id;
    }

    private static int StatusOf(JsonElement result)
        => result.TryGetProperty("status_code", out var s) ? s.GetInt32() : 200;

    [Fact]
    public async Task SetRole_AMissingUserIdIsRejected()
    {
        using var db = TestDb.NewContext();
        var (handler, _) = SetRole(db);

        var result = await handler.ExecuteAsync(TestJson.Element("{}"), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("user_id", Error(result));
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("")]
    [InlineData("root")]
    public async Task SetRole_AnUnknownRoleIsRejected(string role)
    {
        using var db = TestDb.NewContext();
        var id = SeedUser(db);
        var (handler, _) = SetRole(db);

        var result = await handler.ExecuteAsync(TestJson.Element(
            "{\"user_id\":\"" + id + "\",\"role\":\"" + role + "\"}"), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("viewer", db.Users.Single().Role);
    }

    // The privilege guard: in granular mode a non-admin can hold
    // `user.manage`, so without this they could self-promote through chat.
    [Fact]
    public async Task SetRole_OnlyAnAdminCanMintAnotherAdmin()
    {
        using var db = TestDb.NewContext();
        var id = SeedUser(db);
        var (handler, sync) = SetRole(db, "operator");

        var result = await handler.ExecuteAsync(TestJson.Element(
            "{\"user_id\":\"" + id + "\",\"role\":\"admin\"}"), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("viewer", db.Users.Single().Role);
        Assert.Empty(sync.Synced);
    }

    // A non-admin may still hand out the weaker roles.
    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    public async Task SetRole_ANonAdminMayGrantTheWeakerRoles(string role)
    {
        using var db = TestDb.NewContext();
        var id = SeedUser(db);
        var (handler, _) = SetRole(db, "operator");

        var result = await handler.ExecuteAsync(TestJson.Element(
            "{\"user_id\":\"" + id + "\",\"role\":\"" + role + "\"}"), default);

        Assert.True(result.GetProperty("updated").GetBoolean());
        Assert.Equal(role, db.Users.Single().Role);
    }

    [Fact]
    public async Task SetRole_AnAdminCanMintAnAdmin()
    {
        using var db = TestDb.NewContext();
        var id = SeedUser(db);
        var (handler, _) = SetRole(db, "admin");

        var result = await handler.ExecuteAsync(TestJson.Element(
            "{\"user_id\":\"" + id + "\",\"role\":\"ADMIN\"}"), default);

        Assert.True(result.GetProperty("updated").GetBoolean());
        Assert.Equal("admin", db.Users.Single().Role);
    }

    // The built-in grants are a dual-write of the role; skipping the sync
    // leaves the granular checks answering from a stale role.
    [Fact]
    public async Task SetRole_TheBuiltinGrantsAreResynced()
    {
        using var db = TestDb.NewContext();
        var id = SeedUser(db);
        var (handler, sync) = SetRole(db);

        await handler.ExecuteAsync(TestJson.Element(
            "{\"user_id\":\"" + id + "\",\"role\":\"operator\"}"), default);

        Assert.Equal((id, "operator"), Assert.Single(sync.Synced));
    }

    [Fact]
    public async Task SetRole_ADeactivatedUserIsNotFound()
    {
        using var db = TestDb.NewContext();
        var id = SeedUser(db, active: false);
        var (handler, _) = SetRole(db);

        var result = await handler.ExecuteAsync(TestJson.Element(
            "{\"user_id\":\"" + id + "\",\"role\":\"operator\"}"), default);

        Assert.Equal(404, StatusOf(result));
    }

    // ─── validate_ssh_commands ──────────────────────────────────────────

    private sealed class ScriptedValidator : IVendorCommandValidator
    {
        public List<string> Warnings { get; } = new();
        public Task<WorkflowValidationResult> ValidateAsync(
            JsonElement nodes, IReadOnlyCollection<Guid> targetDeviceIds, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ValidateCommandsAsync(
            string deviceType, IReadOnlyList<string> commands, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Warnings);
    }

    private sealed class ScriptedRegistry : IVendorCommandRegistry
    {
        public List<string> Known { get; } = new();
        public Task<KnownStatus> IsKnownAsync(
            string deviceType, string command, CancellationToken ct)
            => Task.FromResult(default(KnownStatus));
        public Task<IReadOnlyList<string>> SuggestSimilarAsync(
            string deviceType, string command, int max, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> KnownCommandsForDeviceTypeAsync(
            string deviceType, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Known);
        public void Invalidate() { }
    }

    private static (ValidateSshCommandsHandler Handler, ScriptedValidator Validator, ScriptedRegistry Registry)
        SshValidator()
    {
        var validator = new ScriptedValidator();
        var registry = new ScriptedRegistry();
        return (new ValidateSshCommandsHandler(
            new FakeUser(),
            validator,
            registry,
            NullLogger<ValidateSshCommandsHandler>.Instance), validator, registry);
    }

    [Theory]
    [InlineData("""{"commands":["show version"]}""", "device_type is required.")]
    [InlineData("""{"device_type":"   ","commands":["show version"]}""", "device_type is required.")]
    [InlineData("""{"device_type":"cisco_ios"}""", "commands list must not be empty.")]
    [InlineData("""{"device_type":"cisco_ios","commands":[]}""", "commands list must not be empty.")]
    public async Task SshValidate_AMissingArgumentIsReportedAsAWarningNotAThrow(string args, string expected)
    {
        var (handler, _, _) = SshValidator();

        var result = await handler.ExecuteAsync(TestJson.Element(args), default);

        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Equal(expected,
            result.GetProperty("warnings").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task SshValidate_KnownCommandsPass()
    {
        var (handler, _, registry) = SshValidator();
        registry.Known.Add("show version");

        var result = await handler.ExecuteAsync(TestJson.Element(
            """{"device_type":"cisco_ios","commands":["show version"]}"""), default);

        Assert.True(result.GetProperty("valid").GetBoolean());
        Assert.Equal(1, result.GetProperty("commands_checked").GetInt32());
    }

    [Fact]
    public async Task SshValidate_TheValidatorsWarningsAreSurfaced()
    {
        var (handler, validator, _) = SshValidator();
        validator.Warnings.Add("unknown command 'shwo version'");

        var result = await handler.ExecuteAsync(TestJson.Element(
            """{"device_type":"cisco_ios","commands":["shwo version"]}"""), default);

        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Contains("shwo version",
            result.GetProperty("warnings").EnumerateArray().Single().GetString());
    }

    // The known-command sample is what the agent offers as a picklist when
    // its draft is wrong — capped so a big catalogue doesn't flood the chat.
    [Fact]
    public async Task SshValidate_TheKnownCommandSampleIsCappedButTheTotalIsReported()
    {
        var (handler, _, registry) = SshValidator();
        for (var i = 0; i < 50; i++) registry.Known.Add($"show thing {i}");

        var result = await handler.ExecuteAsync(TestJson.Element(
            """{"device_type":"cisco_ios","commands":["show version"]}"""), default);

        Assert.Equal(30, result.GetProperty("known_commands").GetArrayLength());
        Assert.Equal(50, result.GetProperty("catalog_total").GetInt32());
    }

    // Non-string entries in the command list are noise from the model, not
    // commands.
    [Fact]
    public async Task SshValidate_NonStringCommandEntriesAreDropped()
    {
        var (handler, _, _) = SshValidator();

        var result = await handler.ExecuteAsync(TestJson.Element(
            """{"device_type":"cisco_ios","commands":["show version",42,null,"  "]}"""), default);

        Assert.Equal(1, result.GetProperty("commands_checked").GetInt32());
    }
}
