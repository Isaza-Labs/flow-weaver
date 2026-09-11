using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The clarifying-questions gate: before the agent builds a workflow it checks
// whether the request actually contains enough to build one. Being too eager
// here annoys the user; being too lax ships a silently mis-configured workflow
// — so the checks are deliberately conservative (ask once, cheaply).
public class EvaluatePromptSufficiencyHandlerTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public EvaluatePromptSufficiencyHandler Build() => new(
            new SnippetRepository(Db),
            new WorkflowRepository(Db),
            new FakeUser(),
            NullLogger<EvaluatePromptSufficiencyHandler>.Instance);

        public Guid SeedSnippet(
            string name = "reboot", string targetMode = "per_device",
            string? inputSchema = null, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Set<Snippet>().Add(new Snippet
            {
                SnippetId = id,
                Name = name,
                Type = "ssh",
                TargetMode = targetMode,
                InputSchema = inputSchema is null ? default : TestJson.Element(inputSchema),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void SeedWorkflow(string name, string environment = "draft")
        {
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = Guid.NewGuid(),
                Name = name,
                Environment = environment,
                Version = 1,
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static JsonElement Args(
        string description, string? candidateName = null,
        IEnumerable<Guid>? snippetIds = null,
        IEnumerable<string>? devices = null, IEnumerable<string>? pools = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["description"] = description,
        };
        if (candidateName is not null) payload["candidate_name"] = candidateName;
        if (snippetIds is not null) payload["proposed_snippet_ids"] = snippetIds.Select(g => g.ToString()).ToList();
        if (devices is not null) payload["proposed_target_devices"] = devices.ToList();
        if (pools is not null) payload["proposed_target_pools"] = pools.ToList();
        return JsonSerializer.SerializeToElement(payload);
    }

    private static async Task<JsonElement> Run(Fixture f, JsonElement args)
        => await f.Build().ExecuteAsync(args, default);

    private static bool Sufficient(JsonElement result) => result.GetProperty("sufficient").GetBoolean();
    private static List<JsonElement> Missing(JsonElement result)
        => result.GetProperty("missing").EnumerateArray().ToList();
    private static List<JsonElement> Ambiguities(JsonElement result)
        => result.GetProperty("ambiguities").EnumerateArray().ToList();

    // ─── empty request ──────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyDescriptionIsInsufficient(string description)
    {
        using var f = new Fixture();

        var result = await Run(f, Args(description));

        Assert.False(Sufficient(result));
        var missing = Assert.Single(Missing(result));
        Assert.Equal("description", missing.GetProperty("field").GetString());
        Assert.Contains("What should this workflow do?", missing.GetProperty("suggested_question").GetString());
    }

    [Fact]
    public async Task ANonEmptyDescriptionWithNothingElseIsSufficient()
    {
        using var f = new Fixture();

        var result = await Run(f, Args("collect the lldp neighbours"));

        Assert.True(Sufficient(result));
        Assert.Empty(Missing(result));
        Assert.Empty(Ambiguities(result));
    }

    // ─── target resolution ──────────────────────────────────────────────

    // A per_device snippet with no devices and no pools cannot run.
    [Fact]
    public async Task PerDeviceSnippetWithoutTargetsAsksForThem()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(name: "reboot", targetMode: "per_device");

        var result = await Run(f, Args("reboot the routers", snippetIds: new[] { snippetId }));

        Assert.False(Sufficient(result));
        var missing = Assert.Single(Missing(result), m => m.GetProperty("field").GetString() == "targets");
        Assert.Contains("reboot", missing.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task PerDeviceSnippetWithDevicesIsSatisfied()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "per_device");

        var result = await Run(f, Args("reboot", snippetIds: new[] { snippetId }, devices: new[] { "rtr-1" }));

        Assert.DoesNotContain(Missing(result), m => m.GetProperty("field").GetString() == "targets");
    }

    // A pool satisfies the requirement just as well as an explicit device.
    [Fact]
    public async Task PerDeviceSnippetWithAPoolIsSatisfied()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "per_device");

        var result = await Run(f, Args("reboot", snippetIds: new[] { snippetId }, pools: new[] { "edge" }));

        Assert.DoesNotContain(Missing(result), m => m.GetProperty("field").GetString() == "targets");
    }

    // A `once` snippet runs without a device, so it must not ask.
    [Fact]
    public async Task NonPerDeviceSnippetDoesNotRequireTargets()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "once");

        var result = await Run(f, Args("generate a report", snippetIds: new[] { snippetId }));

        Assert.True(Sufficient(result));
    }

    // ─── required input keys ────────────────────────────────────────────

    // If the user never mentioned a required field, ask — a silent default is
    // worse than one cheap question.
    [Fact]
    public async Task UnmentionedRequiredKeyIsAskedAbout()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(
            name: "set_vlan", targetMode: "once",
            inputSchema: """{"required":["vlan_id"]}""");

        var result = await Run(f, Args("configure the access ports", snippetIds: new[] { snippetId }));

        Assert.False(Sufficient(result));
        var missing = Assert.Single(Missing(result));
        Assert.Equal("set_vlan.vlan_id", missing.GetProperty("field").GetString());
        Assert.Contains("vlan_id", missing.GetProperty("suggested_question").GetString());
    }

    [Fact]
    public async Task MentionedRequiredKeyIsNotAskedAbout()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(
            targetMode: "once", inputSchema: """{"required":["vlan_id"]}""");

        var result = await Run(f, Args("set vlan_id 42 on the access ports", snippetIds: new[] { snippetId }));

        Assert.True(Sufficient(result));
    }

    // The humanised form counts too: "vlan id" satisfies `vlan_id`.
    [Fact]
    public async Task HumanisedKeyFormCounts()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "once", inputSchema: """{"required":["vlan_id"]}""");

        var result = await Run(f, Args("set vlan id 42", snippetIds: new[] { snippetId }));

        Assert.True(Sufficient(result));
    }

    [Fact]
    public async Task KeyMatchingIsCaseInsensitive()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "once", inputSchema: """{"required":["vlan_id"]}""");

        var result = await Run(f, Args("set VLAN_ID 42", snippetIds: new[] { snippetId }));

        Assert.True(Sufficient(result));
    }

    [Fact]
    public async Task EveryUnmentionedKeyGetsItsOwnQuestion()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(
            targetMode: "once", inputSchema: """{"required":["vlan_id","interface"]}""");

        var result = await Run(f, Args("configure things", snippetIds: new[] { snippetId }));

        Assert.Equal(2, Missing(result).Count);
    }

    [Theory]
    [InlineData(null)]                       // no schema at all
    [InlineData("""{}""")]                   // no `required`
    [InlineData("""{"required":[]}""")]      // empty
    [InlineData("""{"required":"not-array"}""")]
    [InlineData("""{"required":[123,null,""]}""")]  // non-string entries
    public async Task MalformedOrAbsentSchemasAskNothing(string? schema)
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "once", inputSchema: schema);

        var result = await Run(f, Args("do the thing", snippetIds: new[] { snippetId }));

        Assert.True(Sufficient(result));
    }

    // Soft-deleted snippets must not drive questions.
    [Fact]
    public async Task InactiveSnippetsAreIgnored()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(targetMode: "per_device", active: false);

        var result = await Run(f, Args("reboot", snippetIds: new[] { snippetId }));

        Assert.True(Sufficient(result));
    }

    // ─── name collision ─────────────────────────────────────────────────

    // Silently creating a second "daily backup" is how you end up with
    // three of everything.
    [Fact]
    public async Task ExistingWorkflowNameRaisesAnAmbiguity()
    {
        using var f = new Fixture();
        f.SeedWorkflow("daily backup");

        var result = await Run(f, Args("back up the configs", candidateName: "daily backup"));

        Assert.False(Sufficient(result));
        var ambiguity = Assert.Single(Ambiguities(result));
        Assert.Equal("workflow_name_collision", ambiguity.GetProperty("topic").GetString());
        Assert.Contains("different name", ambiguity.GetProperty("suggested_question").GetString());
        Assert.Single(ambiguity.GetProperty("options").EnumerateArray());
    }

    [Fact]
    public async Task AFreeNameRaisesNothing()
    {
        using var f = new Fixture();
        f.SeedWorkflow("something else");

        var result = await Run(f, Args("back up the configs", candidateName: "daily backup"));

        Assert.True(Sufficient(result));
    }

    [Fact]
    public async Task NoCandidateNameSkipsTheCollisionCheck()
    {
        using var f = new Fixture();
        f.SeedWorkflow("daily backup");

        var result = await Run(f, Args("back up the configs"));

        Assert.True(Sufficient(result));
    }

    // ─── production scope ───────────────────────────────────────────────

    // Running against production without saying so explicitly is the single
    // most expensive mistake this gate prevents.
    [Theory]
    [InlineData("run this against production")]
    [InlineData("ejecutar en producción")]
    [InlineData("ejecutar en produccion")]
    [InlineData("push to prod ")]
    [InlineData("deploy to prod")]
    public async Task ProductionMentionWithoutConfirmationAsks(string description)
    {
        using var f = new Fixture();

        var result = await Run(f, Args(description));

        Assert.False(Sufficient(result));
        var ambiguity = Assert.Single(Ambiguities(result));
        Assert.Equal("production_scope", ambiguity.GetProperty("topic").GetString());
        Assert.Equal(new[] { "draft", "qa", "production" },
            ambiguity.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
    }

    // An explicit change-window / approval reference is the confirmation.
    [Theory]
    [InlineData("run in production during the approved change window")]
    [InlineData("producción, ya aprobado por el CAB")]
    [InlineData("production during the maintenance window")]
    [InlineData("production, ventana de cambio del sábado")]
    public async Task ProductionMentionWithConfirmationDoesNotAsk(string description)
    {
        using var f = new Fixture();

        var result = await Run(f, Args(description));

        Assert.True(Sufficient(result));
    }

    // "prod" must be a word, not a substring — "product inventory" is not a
    // production-scope request.
    [Theory]
    [InlineData("build a product inventory report")]
    [InlineData("reproduce the issue")]
    public async Task ProdSubstringsDoNotTriggerTheScopeQuestion(string description)
    {
        using var f = new Fixture();

        Assert.True(Sufficient(await Run(f, Args(description))));
    }

    // ─── combined signals ───────────────────────────────────────────────

    [Fact]
    public async Task MissingItemsAndAmbiguitiesAreReportedTogether()
    {
        using var f = new Fixture();
        f.SeedWorkflow("daily backup");
        var snippetId = f.SeedSnippet(targetMode: "per_device");

        var result = await Run(f, Args(
            "back up the configs in production",
            candidateName: "daily backup",
            snippetIds: new[] { snippetId }));

        Assert.False(Sufficient(result));
        Assert.NotEmpty(Missing(result));
        Assert.Equal(2, Ambiguities(result).Count);   // name collision + prod scope
    }

    // ─── argument parsing ───────────────────────────────────────────────

    // Non-guid entries in the id list are skipped rather than throwing.
    [Fact]
    public async Task MalformedSnippetIdsAreSkipped()
    {
        using var f = new Fixture();
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "do the thing",
            proposed_snippet_ids = new[] { "not-a-guid", "" },
        });

        Assert.True(Sufficient(await Run(f, args)));
    }

    [Fact]
    public async Task WrongTypedArgumentsDegradeToEmptyLists()
    {
        using var f = new Fixture();
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "do the thing",
            proposed_snippet_ids = "not an array",
            proposed_target_devices = 42,
            candidate_name = 7,
        });

        Assert.True(Sufficient(await Run(f, args)));
    }
}
