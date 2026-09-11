using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.WorkflowPlan;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;

namespace flow_weaver_backend.Tests;

// A plan walks draft → awaiting_approval → approved → building → built, and
// every transition is guarded: you cannot approve a draft, edit an approved
// plan, or build one that was never approved. That state machine is the human
// checkpoint before the agent materialises real workflows, so each illegal
// transition must be refused rather than quietly allowed.
public class WorkflowPlanLifecycleTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public WorkflowPlanService Build() => new(
            new RepositoryBase<WorkflowPlanModel>(Db),
            new SnippetRepository(Db),
            new RepositoryBase<WorkflowModel>(Db),
            new UnitOfWork(Db),
            new FakeUser(),
            NullLogger<WorkflowPlanService>.Instance);

        public Guid SeedPlan(
            string status = PlanStatus.Draft,
            string intent = "reboot the edge routers",
            string? steps = null,
            string? servicesToCreate = null)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowPlanModel>().Add(new WorkflowPlanModel
            {
                WorkflowPlanId = id,
                Intent = intent,
                Description = "a description",
                Status = status,
                Steps = steps is null ? default : TestJson.Element(steps),
                ServicesToCreate = servicesToCreate is null ? default : TestJson.Element(servicesToCreate),
                TargetDevices = new List<Guid>(),
                TargetPools = new List<Guid>(),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static WorkflowPlanResponse Ok(ActionResult<WorkflowPlanResponse> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<WorkflowPlanResponse>(result.Value);
    }

    private static WorkflowPlanResponse Created(ActionResult<WorkflowPlanResponse> result)
        => Assert.IsType<WorkflowPlanResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);

    // Every non-draft status, for the "only drafts are editable" theories.
    public static TheoryData<string> NonDraftStatuses => new()
    {
        PlanStatus.AwaitingApproval, PlanStatus.Approved, PlanStatus.Rejected,
        PlanStatus.Building, PlanStatus.Built, PlanStatus.Failed,
    };

    public static TheoryData<string> NonAwaitingStatuses => new()
    {
        PlanStatus.Draft, PlanStatus.Approved, PlanStatus.Rejected,
        PlanStatus.Building, PlanStatus.Built, PlanStatus.Failed,
    };

    // ─── create ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_MissingIntent_Is400(string intent)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateWorkflowPlan { Intent = intent });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(f.Db.Set<WorkflowPlanModel>());
    }

    // A new plan always starts as a draft — it must never skip the approval gate.
    [Fact]
    public async Task Create_StartsAsADraft()
    {
        using var f = new Fixture();

        var body = Created(await f.Build().PostAsync(new CreateWorkflowPlan { Intent = "do a thing" }));

        Assert.Equal(PlanStatus.Draft, body.Status);
        var row = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal("do a thing", row.Intent);
    }

    [Fact]
    public async Task Create_KeepsSuppliedCollections()
    {
        using var f = new Fixture();
        var deviceId = Guid.NewGuid();
        var poolId = Guid.NewGuid();
        var dto = new CreateWorkflowPlan
        {
            Intent = "patch",
            TargetDevices = new List<Guid> { deviceId },
            TargetPools = new List<Guid> { poolId },
        };

        await f.Build().PostAsync(dto);

        var row = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal(deviceId, Assert.Single(row.TargetDevices));
        Assert.Equal(poolId, Assert.Single(row.TargetPools));
    }

    // ─── read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_UnknownPlan_Is404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task List_ReturnsTheStoredPlans()
    {
        using var f = new Fixture();
        f.SeedPlan(intent: "a");
        f.SeedPlan(intent: "b");

        var ok = Assert.IsType<OkObjectResult>((await f.Build().GetAsync()).Result);
        var body = Assert.IsType<ListResponse<WorkflowPlanResponse>>(ok.Value);

        Assert.Equal(2, body.Total);
    }

    // ─── update: drafts only ────────────────────────────────────────────

    [Fact]
    public async Task Update_AppliesSuppliedFieldsToADraft()
    {
        using var f = new Fixture();
        var id = f.SeedPlan();

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflowPlan
        {
            Intent = "new intent",
            Description = "new description",
        }));

        Assert.Equal("new intent", body.Intent);
        Assert.Equal("new description", body.Description);
    }

    [Fact]
    public async Task Update_OmittedFieldsAreLeftAlone()
    {
        using var f = new Fixture();
        var id = f.SeedPlan();

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflowPlan { Intent = "renamed" }));

        Assert.Equal("a description", body.Description);
    }

    // Once submitted, a plan is frozen — otherwise the approver could be shown
    // one thing and a different plan gets built.
    [Theory]
    [MemberData(nameof(NonDraftStatuses))]
    public async Task Update_NonDraft_Is409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        var result = await f.Build().UpdateAsync(id, new UpdateWorkflowPlan { Intent = "sneaky edit" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("reboot the edge routers", (await f.Db.Set<WorkflowPlanModel>().SingleAsync()).Intent);
    }

    [Fact]
    public async Task Update_UnknownPlan_Is404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>(
            (await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateWorkflowPlan())).Result);
    }

    // ─── delete ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PlanStatus.Draft)]
    [InlineData(PlanStatus.AwaitingApproval)]
    [InlineData(PlanStatus.Rejected)]
    [InlineData(PlanStatus.Failed)]
    public async Task Delete_AllowedStatusesSoftDelete(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        Ok(await f.Build().DeleteAsync(id));

        Assert.False((await f.Db.Set<WorkflowPlanModel>().SingleAsync()).IsActive);
    }

    // A plan that already produced (or is producing) a workflow keeps its audit
    // trail.
    [Theory]
    [InlineData(PlanStatus.Approved)]
    [InlineData(PlanStatus.Building)]
    [InlineData(PlanStatus.Built)]
    public async Task Delete_ProducingStatuses_Is409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        Assert.IsType<ConflictObjectResult>((await f.Build().DeleteAsync(id)).Result);
        Assert.True((await f.Db.Set<WorkflowPlanModel>().SingleAsync()).IsActive);
    }

    // ─── submit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_MovesADraftToAwaitingApproval()
    {
        using var f = new Fixture();
        var id = f.SeedPlan();

        var body = Ok(await f.Build().SubmitForApprovalAsync(id));

        Assert.Equal(PlanStatus.AwaitingApproval, body.Status);
    }

    [Theory]
    [MemberData(nameof(NonDraftStatuses))]
    public async Task Submit_NonDraft_Is409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        Assert.IsType<ConflictObjectResult>((await f.Build().SubmitForApprovalAsync(id)).Result);
    }

    // ─── approve ────────────────────────────────────────────────────────

    // The approver's identity is the whole point of the gate, so it is required.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Approve_WithoutApprover_Is400(string approver)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.AwaitingApproval);

        var result = await f.Build().ApproveAsync(id, new ApprovePlan { ApprovedBy = approver });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(PlanStatus.AwaitingApproval, (await f.Db.Set<WorkflowPlanModel>().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_RecordsWhoApprovedAndWhen()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.AwaitingApproval);

        var body = Ok(await f.Build().ApproveAsync(id, new ApprovePlan { ApprovedBy = "alice" }));

        Assert.Equal(PlanStatus.Approved, body.Status);
        var row = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal("alice", row.ApprovedBy);
        Assert.NotNull(row.ApprovedAt);
    }

    // A draft cannot skip straight to approved — it must be submitted first.
    [Theory]
    [MemberData(nameof(NonAwaitingStatuses))]
    public async Task Approve_WrongStatus_Is409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        Assert.IsType<ConflictObjectResult>(
            (await f.Build().ApproveAsync(id, new ApprovePlan { ApprovedBy = "alice" })).Result);
    }

    // ─── reject ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_WithoutApprover_Is400()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.AwaitingApproval);

        var result = await f.Build().RejectAsync(id, new RejectPlan { ApprovedBy = "", Reason = "nope" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // A rejection without a reason gives the author nothing to act on.
    [Fact]
    public async Task Reject_WithoutReason_Is400()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.AwaitingApproval);

        var result = await f.Build().RejectAsync(id, new RejectPlan { ApprovedBy = "alice", Reason = "" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Reject_RecordsTheReasonAndReviewer()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.AwaitingApproval);

        var body = Ok(await f.Build().RejectAsync(
            id, new RejectPlan { ApprovedBy = "alice", Reason = "too risky mid-week" }));

        Assert.Equal(PlanStatus.Rejected, body.Status);
        var row = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal("too risky mid-week", row.RejectionReason);
        Assert.Equal("alice", row.ApprovedBy);
    }

    [Theory]
    [MemberData(nameof(NonAwaitingStatuses))]
    public async Task Reject_WrongStatus_Is409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        Assert.IsType<ConflictObjectResult>(
            (await f.Build().RejectAsync(id, new RejectPlan { ApprovedBy = "a", Reason = "r" })).Result);
    }

    // ─── build ──────────────────────────────────────────────────────────

    // Building is what turns the plan into a real workflow, so it must require
    // an approval — this is the human checkpoint.
    [Theory]
    [InlineData(PlanStatus.Draft)]
    [InlineData(PlanStatus.AwaitingApproval)]
    [InlineData(PlanStatus.Rejected)]
    [InlineData(PlanStatus.Built)]
    [InlineData(PlanStatus.Failed)]
    public async Task Build_WithoutApproval_Is409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: status);

        Assert.IsType<ConflictObjectResult>((await f.Build().BuildAsync(id)).Result);
        Assert.Empty(f.Db.Set<WorkflowModel>());
    }

    [Fact]
    public async Task Build_UnknownPlan_Is404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().BuildAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Build_CreatesAWorkflowAndMarksThePlanBuilt()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved, intent: "reboot routers");

        var body = Ok(await f.Build().BuildAsync(id));

        Assert.Equal(PlanStatus.Built, body.Status);
        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.Equal("reboot routers", wf.Name);
        Assert.Equal("draft", wf.Environment);
        Assert.Equal(1, wf.Version);
        Assert.Contains(id.ToString(), wf.ChangeSummary);
        // Attribution: agent-built workflows must not land with created_by null.
        Assert.Equal("tester", wf.CreatedBy);

        var plan = await f.Db.Set<WorkflowPlanModel>().SingleAsync();
        Assert.Equal(wf.WorkflowId, plan.WorkflowId);
        Assert.NotNull(plan.ExecutedAt);
    }

    // An empty plan still produces a runnable skeleton: start → end.
    [Fact]
    public async Task Build_EmptyPlanProducesStartAndEndOnly()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved);

        await f.Build().BuildAsync(id);

        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        var nodeIds = wf.Nodes.EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "__start__", "__end__" }, nodeIds);
        var edge = Assert.Single(wf.Edges.EnumerateArray());
        Assert.Equal("__start__", edge.GetProperty("source").GetString());
        Assert.Equal("__end__", edge.GetProperty("target").GetString());
    }

    // Steps become a linear chain wired start → … → end.
    [Fact]
    public async Task Build_StepsBecomeALinearChain()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved, steps: """
            [{"name":"backup","service_type":"ssh"},
             {"name":"reboot","service_type":"ssh"}]
            """);

        await f.Build().BuildAsync(id);

        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        var nodes = wf.Nodes.EnumerateArray().ToList();
        Assert.Equal(4, nodes.Count);   // start + 2 steps + end

        var edges = wf.Edges.EnumerateArray()
            .Select(e => (e.GetProperty("source").GetString(), e.GetProperty("target").GetString()))
            .ToList();
        Assert.Equal(3, edges.Count);
        Assert.Equal("__start__", edges[0].Item1);
        Assert.Equal("__end__", edges[^1].Item2);
        Assert.All(edges, e => Assert.NotEqual(e.Item1, e.Item2));
    }

    // Services declared in the plan are materialised as snippets, and the step
    // nodes point at the new snippet ids rather than a bare type string.
    [Fact]
    public async Task Build_CreatesSnippetsAndWiresThemIntoTheNodes()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(
            status: PlanStatus.Approved,
            servicesToCreate: """[{"name":"backup","type":"ssh","description":"saves config"}]""",
            steps: """[{"name":"backup","service_type":"ssh"}]""");

        await f.Build().BuildAsync(id);

        var snippet = await f.Db.Set<Snippet>().SingleAsync();
        Assert.Equal("backup", snippet.Name);
        Assert.Equal("ssh", snippet.Type);
        Assert.Equal("saves config", snippet.Description);

        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        var stepNode = wf.Nodes.EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "node-1");
        Assert.Equal(snippet.SnippetId.ToString(), stepNode.GetProperty("snippet_id").GetString());
    }

    // An existing snippet with the same name is reused, not duplicated.
    [Fact]
    public async Task Build_ReusesAnExistingSnippetByName()
    {
        using var f = new Fixture();
        var existingId = Guid.NewGuid();
        f.Db.Set<Snippet>().Add(new Snippet
        {
            SnippetId = existingId,
            Name = "backup",
            Type = "ssh",
            IsActive = true,
        });
        f.Db.SaveChanges();
        var id = f.SeedPlan(
            status: PlanStatus.Approved,
            servicesToCreate: """[{"name":"backup","type":"ssh"}]""",
            steps: """[{"name":"backup","service_type":"ssh"}]""");

        await f.Build().BuildAsync(id);

        Assert.Single(f.Db.Set<Snippet>());
        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        var stepNode = wf.Nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "node-1");
        Assert.Equal(existingId.ToString(), stepNode.GetProperty("snippet_id").GetString());
    }

    // Incomplete service declarations are skipped rather than creating junk rows.
    [Theory]
    [InlineData("""[{"type":"ssh"}]""")]
    [InlineData("""[{"name":"x"}]""")]
    [InlineData("""[{"name":"","type":"ssh"}]""")]
    public async Task Build_SkipsIncompleteServiceDeclarations(string services)
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved, servicesToCreate: services);

        await f.Build().BuildAsync(id);

        Assert.Empty(f.Db.Set<Snippet>());
    }

    // A step's config rides along into the node's config_overrides.
    [Fact]
    public async Task Build_StepConfigBecomesConfigOverrides()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved, steps: """
            [{"name":"reboot","service_type":"ssh","config":{"command":"reload"}}]
            """);

        await f.Build().BuildAsync(id);

        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        var node = wf.Nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "node-1");
        Assert.Equal("reload", node.GetProperty("config_overrides").GetProperty("command").GetString());
    }

    // A step with no matching service falls back to the raw type string, so the
    // graph still builds and the gap is visible in the editor.
    [Fact]
    public async Task Build_UnmatchedStepFallsBackToTheServiceType()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved, steps: """
            [{"name":"unknown","service_type":"http_call"}]
            """);

        await f.Build().BuildAsync(id);

        var wf = await f.Db.Set<WorkflowModel>().SingleAsync();
        var node = wf.Nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "node-1");
        Assert.Equal("http_call", node.GetProperty("snippet_id").GetString());
    }

    [Fact]
    public async Task Build_IsRefusedASecondTime()
    {
        using var f = new Fixture();
        var id = f.SeedPlan(status: PlanStatus.Approved);
        var svc = f.Build();
        await svc.BuildAsync(id);

        Assert.IsType<ConflictObjectResult>((await svc.BuildAsync(id)).Result);
        Assert.Single(f.Db.Set<WorkflowModel>());
    }
}
