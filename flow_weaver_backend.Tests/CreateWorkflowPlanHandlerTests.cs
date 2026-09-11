using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;

namespace flow_weaver_backend.Tests;

// The governance-plan tool. Its hard gate exists because the agent used to
// route "create a workflow that pings X" through five approval steps when the
// user just wanted a draft: plans are for qa/production review only, and a
// draft request must be sent back to create_workflow. It also materialises one
// PlanFeature per step so the checklist survives a conversation resume.
public class CreateWorkflowPlanHandlerTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public CreateWorkflowPlanHandler Build() => new(
            new RepositoryBase<WorkflowPlanModel>(Db),
            new PlanFeatureRepository(Db),
            new FakeUser(),
            NullLogger<CreateWorkflowPlanHandler>.Instance);

        public void Dispose() => Db.Dispose();
    }

    private static JsonElement Args(
        string? intent = "roll out the new ACL",
        string? targetEnvironment = "qa",
        params (string Key, object? Value)[] extra)
    {
        var payload = new Dictionary<string, object?>();
        if (intent is not null) payload["intent"] = intent;
        if (targetEnvironment is not null) payload["target_environment"] = targetEnvironment;
        foreach (var (k, v) in extra) payload[k] = v;
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string? ErrorOf(JsonElement result)
        => result.TryGetProperty("error", out var e) ? e.GetString() : null;

    private static async Task<JsonElement> Run(Fixture f, JsonElement args)
        => await f.Build().ExecuteAsync(args, default);

    // ─── the draft gate ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingIntentIsAnError(string? intent)
    {
        using var f = new Fixture();

        Assert.Contains("intent is required", ErrorOf(await Run(f, Args(intent: intent))));
    }

    // Without an explicit environment the agent gets pointed at the direct
    // create path instead of silently opening a governance plan.
    [Fact]
    public async Task MissingTargetEnvironmentRedirectsToCreateWorkflow()
    {
        using var f = new Fixture();

        var error = ErrorOf(await Run(f, Args(targetEnvironment: null)));

        Assert.Contains("target_environment is required", error);
        Assert.Contains("create_workflow", error);
        Assert.Empty(f.Db.Set<WorkflowPlanModel>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankTargetEnvironmentIsAlsoRedirected(string env)
    {
        using var f = new Fixture();

        Assert.Contains("target_environment is required", ErrorOf(await Run(f, Args(targetEnvironment: env))));
    }

    // The whole point of the gate: draft never goes through the plan flow.
    [Fact]
    public async Task DraftIsExplicitlyRefusedWithGuidance()
    {
        using var f = new Fixture();

        var error = ErrorOf(await Run(f, Args(targetEnvironment: "draft")));

        Assert.Contains("not allowed", error);
        Assert.Contains("create_workflow", error);
        Assert.Contains("qa/production", error);
        Assert.Empty(f.Db.Set<WorkflowPlanModel>());
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("dev")]
    [InlineData("prod")]
    public async Task UnknownEnvironmentsAreRefused(string env)
    {
        using var f = new Fixture();

        Assert.Contains("not allowed", ErrorOf(await Run(f, Args(targetEnvironment: env))));
    }

    [Theory]
    [InlineData("qa")]
    [InlineData("production")]
    [InlineData("QA")]
    [InlineData("Production")]
    public async Task ReviewEnvironmentsAreAcceptedCaseInsensitively(string env)
    {
        using var f = new Fixture();

        var result = await Run(f, Args(targetEnvironment: env));

        Assert.Null(ErrorOf(result));
        Assert.Equal(env, result.GetProperty("target_environment").GetString());
    }

    // ─── plan creation ──────────────────────────────────────────────────

    // A new plan always starts as a draft awaiting submission — it must never
    // skip the approval chain.
    [Fact]
    public async Task PlanIsCreatedAsADraftWithTheNextStepSpelledOut()
    {
        using var f = new Fixture();

        var result = await Run(f, Args());

        Assert.Equal("draft", result.GetProperty("status").GetString());
        Assert.Equal("submit_plan_for_approval", result.GetProperty("next_step").GetString());
        Assert.Contains("approve_plan", result.GetProperty("message").GetString());

        var plan = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal("roll out the new ACL", plan.Intent);
        Assert.Equal(result.GetProperty("plan_id").GetGuid(), plan.WorkflowPlanId);
    }

    [Fact]
    public async Task OptionalNarrativeFieldsAreStored()
    {
        using var f = new Fixture();

        await Run(f, Args(extra: new (string, object?)[]
        {
            ("description", "phased rollout"),
            ("risks", new[] { "may drop management access" }),
        }));

        var plan = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal("phased rollout", plan.Description);
        Assert.Equal(JsonValueKind.Array, plan.Risks.ValueKind);
    }

    [Fact]
    public async Task TargetGuidListsAreParsed()
    {
        using var f = new Fixture();
        var deviceId = Guid.NewGuid();
        var poolId = Guid.NewGuid();

        await Run(f, Args(extra: new (string, object?)[]
        {
            ("target_devices", new[] { deviceId.ToString() }),
            ("target_pools", new[] { poolId.ToString() }),
        }));

        var plan = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal(deviceId, Assert.Single(plan.TargetDevices));
        Assert.Equal(poolId, Assert.Single(plan.TargetPools));
    }

    // Malformed ids are skipped rather than throwing mid-plan.
    [Fact]
    public async Task MalformedTargetIdsAreSkipped()
    {
        using var f = new Fixture();
        var good = Guid.NewGuid();

        await Run(f, Args(extra: ("target_devices", new object?[] { good.ToString(), "not-a-guid", 42, null })));

        Assert.Equal(good, Assert.Single((await f.Db.Set<WorkflowPlanModel>().SingleAsync()).TargetDevices));
    }

    [Theory]
    [InlineData("\"not an array\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task NonArrayTargetsBecomeEmptyLists(string raw)
    {
        using var f = new Fixture();
        var args = TestJson.Element(
            "{\"intent\":\"x\",\"target_environment\":\"qa\",\"target_devices\":" + raw + "}");

        await Run(f, args);

        Assert.Empty((await f.Db.Set<WorkflowPlanModel>().SingleAsync()).TargetDevices);
    }

    // ─── plan features (the resumable checklist) ────────────────────────

    // One feature per step, pre-saved as pending, so list_plan_features can
    // rebuild the checklist after a conversation resume.
    [Fact]
    public async Task OneFeatureIsMaterialisedPerStepInOrder()
    {
        using var f = new Fixture();

        await Run(f, Args(extra: ("steps", new object[]
        {
            new { name = "backup", service_type = "ssh" },
            new { name = "apply", service_type = "python_snippet" },
        })));

        var features = await f.Db.Set<PlanFeature>().OrderBy(x => x.Ordinal).ToListAsync();
        Assert.Equal(2, features.Count);
        Assert.Equal("backup", features[0].Title);
        Assert.Equal("ssh", features[0].SnippetType);
        Assert.Equal(0, features[0].Ordinal);
        Assert.Equal("apply", features[1].Title);
        Assert.Equal(1, features[1].Ordinal);
        Assert.All(features, x => Assert.Equal(PlanFeatureStatus.Pending, x.Status));
    }

    [Fact]
    public async Task FeaturesAreLinkedToThePlanAndActive()
    {
        using var f = new Fixture();

        var result = await Run(f, Args(extra: ("steps", new object[] { new { name = "s" } })));

        var feature = await f.Db.Set<PlanFeature>().SingleAsync();
        Assert.Equal(result.GetProperty("plan_id").GetGuid(), feature.WorkflowPlanId);
        Assert.True(feature.IsActive);
    }

    // A step with no usable name still gets a stable placeholder title so the
    // checklist stays readable.
    [Theory]
    [InlineData("""[{"service_type":"ssh"}]""")]
    [InlineData("""[{"name":""}]""")]
    [InlineData("""[{"name":"   "}]""")]
    [InlineData("""[{"name":123}]""")]
    public async Task UnnamedStepsGetAPositionalTitle(string steps)
    {
        using var f = new Fixture();
        var args = TestJson.Element(
            "{\"intent\":\"x\",\"target_environment\":\"qa\",\"steps\":" + steps + "}");

        await Run(f, args);

        Assert.Equal("step_1", (await f.Db.Set<PlanFeature>().SingleAsync()).Title);
    }

    // Non-object entries don't produce phantom checklist rows.
    [Fact]
    public async Task NonObjectStepsAreSkipped()
    {
        using var f = new Fixture();
        var args = TestJson.Element("""
            {"intent":"x","target_environment":"qa","steps":["a string",42,null,{"name":"real"}]}
            """);

        await Run(f, args);

        var feature = await f.Db.Set<PlanFeature>().SingleAsync();
        Assert.Equal("real", feature.Title);
        Assert.Equal(0, feature.Ordinal);   // ordinals count only real steps
    }

    [Theory]
    [InlineData("""{"intent":"x","target_environment":"qa"}""")]
    [InlineData("""{"intent":"x","target_environment":"qa","steps":[]}""")]
    [InlineData("""{"intent":"x","target_environment":"qa","steps":"not an array"}""")]
    public async Task NoStepsMeansNoFeatures(string args)
    {
        using var f = new Fixture();

        await Run(f, TestJson.Element(args));

        Assert.Empty(f.Db.Set<PlanFeature>());
        Assert.Single(f.Db.Set<WorkflowPlanModel>());   // ...but the plan still exists
    }

    [Fact]
    public async Task StepsAreAlsoStoredOnThePlanItself()
    {
        using var f = new Fixture();

        await Run(f, Args(extra: ("steps", new object[] { new { name = "backup" } })));

        var plan = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal(JsonValueKind.Array, plan.Steps.ValueKind);
        Assert.Equal("backup", plan.Steps[0].GetProperty("name").GetString());
    }

    // ─── metadata ───────────────────────────────────────────────────────

    [Fact]
    public void HandlerAdvertisesItsName()
    {
        using var f = new Fixture();

        Assert.Equal("create_workflow_plan", f.Build().Name);
    }
}
