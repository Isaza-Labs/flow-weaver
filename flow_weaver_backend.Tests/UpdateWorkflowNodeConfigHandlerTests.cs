using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The "Edit with AI" write path. This is the one tool that lets the agent
// mutate a live workflow, so its rails matter: production stays immutable, the
// merge is SHALLOW (keys the agent didn't mention must survive), references are
// validated before persisting, and the handler must never create/delete nodes
// or touch edges.
public class UpdateWorkflowNodeConfigHandlerTests
{

    private sealed class ScriptedReferenceValidator : IWorkflowReferenceValidator
    {
        public WorkflowValidationResult Result { get; set; } = WorkflowValidationResult.Ok();
        public Task<WorkflowValidationResult> ValidateAsync(
            JsonElement nodes, CancellationToken ct, JsonElement? previousNodes = null)
            => Task.FromResult(Result);
        public Task<WorkflowValidationResult> ValidateWithContextAsync(
            JsonElement nodes, string? name, string? description, CancellationToken ct,
            JsonElement? previousNodes = null)
            => Task.FromResult(Result);
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ScriptedReferenceValidator References { get; } = new();

        public UpdateWorkflowNodeConfigHandler Build() => new(
            new RepositoryBase<WorkflowModel>(Db),
            new FakeUser(),
            new FakeAudit(),
            References,
            NullLogger<UpdateWorkflowNodeConfigHandler>.Instance);

        public Guid SeedWorkflow(
            string nodes = """[{"id":"n1","snippet_id":"s1","config_overrides":{"command":"show ver","timeout":30}}]""",
            string environment = "draft",
            bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "wf",
                Environment = environment,
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element("""[{"source":"n1","target":"n2"}]"""),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static JsonElement Args(Guid workflowId, string nodeId, string overrides)
        => TestJson.Element($$"""
            {"workflow_id":"{{workflowId}}","node_id":"{{nodeId}}","config_overrides":{{overrides}}}
            """);

    private static string? ErrorOf(JsonElement result)
        => result.TryGetProperty("error", out var e) ? e.GetString() : null;

    private static async Task<JsonElement> Run(Fixture f, JsonElement args)
        => await f.Build().ExecuteAsync(args, default);

    // ─── argument validation ────────────────────────────────────────────

    [Theory]
    [InlineData("""{"node_id":"n1","config_overrides":{}}""")]                       // no workflow_id
    [InlineData("""{"workflow_id":"not-a-guid","node_id":"n1","config_overrides":{}}""")]
    public async Task MissingOrInvalidWorkflowId_IsAnError(string args)
    {
        using var f = new Fixture();

        Assert.Contains("workflow_id", ErrorOf(await Run(f, TestJson.Element(args))));
    }

    [Fact]
    public async Task MissingNodeId_IsAnError()
    {
        using var f = new Fixture();
        var args = TestJson.Element(
            "{\"workflow_id\":\"" + Guid.NewGuid() + "\",\"config_overrides\":{}}");

        Assert.Contains("node_id", ErrorOf(await Run(f, args)));
    }

    [Fact]
    public async Task MissingConfigOverrides_IsAnError()
    {
        using var f = new Fixture();
        var args = TestJson.Element($$"""{"workflow_id":"{{Guid.NewGuid()}}","node_id":"n1"}""");

        Assert.Contains("config_overrides", ErrorOf(await Run(f, args)));
    }

    // Some models serialise the inner object as a JSON *string*; parsing it
    // saves a wasted turn instead of bouncing the call back.
    [Fact]
    public async Task ConfigOverridesAsAJsonStringIsParsedLeniently()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var args = TestJson.Element($$"""
            {"workflow_id":"{{id}}","node_id":"n1","config_overrides":"{\"command\":\"show lldp\"}"}
            """);

        var result = await Run(f, args);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("show lldp",
            result.GetProperty("config_overrides_after").GetProperty("command").GetString());
    }

    [Fact]
    public async Task ConfigOverridesStringThatIsNotAnObject_IsAnError()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var args = TestJson.Element($$"""
            {"workflow_id":"{{id}}","node_id":"n1","config_overrides":"[1,2]"}
            """);

        Assert.Contains("didn't decode to an object", ErrorOf(await Run(f, args)));
    }

    [Fact]
    public async Task ConfigOverridesStringThatIsNotJson_IsAnError()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var args = TestJson.Element($$"""
            {"workflow_id":"{{id}}","node_id":"n1","config_overrides":"not json at all"}
            """);

        Assert.Contains("didn't parse", ErrorOf(await Run(f, args)));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task ConfigOverridesOfTheWrongType_IsAnError(string overrides)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        Assert.Contains("must be a JSON object", ErrorOf(await Run(f, Args(id, "n1", overrides))));
    }

    // ─── target resolution ──────────────────────────────────────────────

    [Fact]
    public async Task UnknownWorkflow_IsAnError()
    {
        using var f = new Fixture();

        Assert.Equal("workflow not found",
            ErrorOf(await Run(f, Args(Guid.NewGuid(), "n1", """{"a":1}"""))));
    }

    [Fact]
    public async Task SoftDeletedWorkflow_IsAnError()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(active: false);

        Assert.Equal("workflow not found", ErrorOf(await Run(f, Args(id, "n1", """{"a":1}"""))));
    }

    [Fact]
    public async Task UnknownNode_IsAnError()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        Assert.Contains("not found in workflow", ErrorOf(await Run(f, Args(id, "ghost", """{"a":1}"""))));
    }

    [Fact]
    public async Task WorkflowWithoutANodesArray_IsAnError()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: """{"not":"an array"}""");

        Assert.Contains("no nodes array", ErrorOf(await Run(f, Args(id, "n1", """{"a":1}"""))));
    }

    // ─── production immutability ────────────────────────────────────────

    // The same rule WorkflowService enforces — the agent must not have a side
    // door into production.
    [Theory]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    public async Task ProductionWorkflowIsRefused(string environment)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: environment);

        var result = await Run(f, Args(id, "n1", """{"command":"rm -rf"}"""));

        Assert.Contains("immutable", ErrorOf(result));
        // ...and nothing was written.
        var row = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.Equal("show ver", row.Nodes[0].GetProperty("config_overrides").GetProperty("command").GetString());
    }

    // ─── the shallow merge ──────────────────────────────────────────────

    // The contract the tool description promises: pass only what changes.
    [Fact]
    public async Task MergeIsShallowAndKeepsUnmentionedKeys()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await Run(f, Args(id, "n1", """{"command":"show system lldp neighbor"}"""));

        var after = result.GetProperty("config_overrides_after");
        Assert.Equal("show system lldp neighbor", after.GetProperty("command").GetString());
        Assert.Equal(30, after.GetProperty("timeout").GetInt32());   // untouched
    }

    [Fact]
    public async Task MergeAddsNewKeys()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await Run(f, Args(id, "n1", """{"device_type":"nokia_srl"}"""));

        var after = result.GetProperty("config_overrides_after");
        Assert.Equal("nokia_srl", after.GetProperty("device_type").GetString());
        Assert.Equal("show ver", after.GetProperty("command").GetString());
    }

    [Fact]
    public async Task BeforeSnapshotIsReturnedForReview()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await Run(f, Args(id, "n1", """{"command":"new"}"""));

        Assert.Equal("show ver",
            result.GetProperty("config_overrides_before").GetProperty("command").GetString());
    }

    [Fact]
    public async Task ChangeIsPersisted()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        await Run(f, Args(id, "n1", """{"command":"persisted"}"""));

        var row = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.Equal("persisted",
            row.Nodes[0].GetProperty("config_overrides").GetProperty("command").GetString());
    }

    // A node with no config_overrides at all gets one.
    [Fact]
    public async Task NodeWithoutConfigOverridesGetsThem()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: """[{"id":"n1","snippet_id":"s1"}]""");

        var result = await Run(f, Args(id, "n1", """{"command":"first"}"""));

        Assert.Equal("first",
            result.GetProperty("config_overrides_after").GetProperty("command").GetString());
    }

    // Scope guard: the handler must not add, remove or reorder nodes...
    [Fact]
    public async Task OtherNodesAreLeftUntouched()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: """
            [{"id":"n1","config_overrides":{"a":1}},
             {"id":"n2","config_overrides":{"b":2}}]
            """);

        await Run(f, Args(id, "n1", """{"a":99}"""));

        var nodes = (await f.Db.Set<WorkflowModel>().SingleAsync()).Nodes;
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.Equal("n1", nodes[0].GetProperty("id").GetString());
        Assert.Equal("n2", nodes[1].GetProperty("id").GetString());
        Assert.Equal(2, nodes[1].GetProperty("config_overrides").GetProperty("b").GetInt32());
    }

    // ...nor touch the edges.
    [Fact]
    public async Task EdgesAreNeverModified()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        await Run(f, Args(id, "n1", """{"a":1}"""));

        var edges = (await f.Db.Set<WorkflowModel>().SingleAsync()).Edges;
        Assert.Single(edges.EnumerateArray());
        Assert.Equal("n1", edges[0].GetProperty("source").GetString());
    }

    // Non-config keys on the target node survive the rewrite.
    [Fact]
    public async Task TargetNodesOtherFieldsSurvive()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(
            nodes: """[{"id":"n1","snippet_id":"s1","x":10,"y":20,"config_overrides":{"a":1}}]""");

        await Run(f, Args(id, "n1", """{"a":2}"""));

        var node = (await f.Db.Set<WorkflowModel>().SingleAsync()).Nodes[0];
        Assert.Equal("s1", node.GetProperty("snippet_id").GetString());
        Assert.Equal(10, node.GetProperty("x").GetInt32());
        Assert.Equal(20, node.GetProperty("y").GetInt32());
    }

    // ─── reference validation gate ──────────────────────────────────────

    // Without this gate the agent could overwrite a good node with a
    // placeholder integration_id and only find out at the next run.
    [Fact]
    public async Task InvalidReferencesBlockThePersistAndReportDetails()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.References.Result = WorkflowValidationResult.Invalid(new[] { "integration_id 'TODO' is not a GUID" });

        var result = await Run(f, Args(id, "n1", """{"integration_id":"TODO"}"""));

        Assert.Equal("references_invalid", ErrorOf(result));
        Assert.Contains("not a GUID", result.GetProperty("details")[0].GetString());

        // The original config survives.
        var row = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.False(row.Nodes[0].GetProperty("config_overrides").TryGetProperty("integration_id", out _));
    }

    [Fact]
    public async Task ValidReferencesAllowThePersist()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.References.Result = WorkflowValidationResult.Ok();

        var result = await Run(f, Args(id, "n1", """{"command":"ok"}"""));

        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    // ─── metadata ───────────────────────────────────────────────────────

    [Fact]
    public void HandlerAdvertisesItsNameAndSchema()
    {
        using var f = new Fixture();
        var handler = f.Build();

        Assert.Equal("update_workflow_node_config", handler.Name);
        var required = handler.ParametersSchema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "workflow_id", "node_id", "config_overrides" }, required);
    }
}
