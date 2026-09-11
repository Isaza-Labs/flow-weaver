using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// Grades a workflow's acceptance tests against its recent runs. The rule that
// keeps this honest is the 24-hour window: grading against an older run would
// be evidence about a DIFFERENT version of the DAG, so a stale run counts as
// "no evidence" rather than a pass. A false green here is the worst outcome —
// the agent then promotes on the strength of it.
public class RunAcceptanceTestsHandlerTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public RunAcceptanceTestsHandler Build() => new(
            new RepositoryBase<WorkflowModel>(Db),
            new WorkflowAcceptanceTestRepository(Db),
            new WorkflowRunRepository(Db),
            new StepRunRepository(Db),
            new FakeUser(),
            NullLogger<RunAcceptanceTestsHandler>.Instance);

        public Guid SeedWorkflow(bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "wf",
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedTest(
            Guid workflowId, string name = "happy path",
            string inputs = "{}", string assertions = "[]")
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowAcceptanceTest>().Add(new WorkflowAcceptanceTest
            {
                WorkflowAcceptanceTestId = id,
                WorkflowId = workflowId,
                Name = name,
                Inputs = TestJson.Element(inputs),
                Assertions = TestJson.Element(assertions),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedRun(
            Guid workflowId, string input = "{}", string status = RunStatus.Completed,
            DateTime? completedAt = null)
        {
            var id = Guid.NewGuid();
            Db.WorkflowRuns.Add(new WorkflowRun
            {
                WorkflowRunId = id,
                WorkflowId = workflowId,
                Status = status,
                InputPayload = TestJson.Element(input),
                CompletedAt = completedAt ?? DateTime.UtcNow,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void SeedStep(Guid runId, string nodeId, string status = StepStatus.Completed, string? output = null)
        {
            Db.StepRuns.Add(new StepRun
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = runId,
                NodeId = nodeId,
                Status = status,
                OutputPayload = output is null ? default : TestJson.Element(output),
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static JsonElement Args(Guid workflowId)
        => TestJson.Element("{\"workflow_id\":\"" + workflowId + "\"}");

    private static async Task<JsonElement> Run(Fixture f, Guid workflowId)
        => await f.Build().ExecuteAsync(Args(workflowId), default);

    private static JsonElement ResultFor(JsonElement response, string name)
        => response.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == name);

    // ─── argument + target validation ───────────────────────────────────

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"workflow_id":"not-a-guid"}""")]
    [InlineData("""{"workflow_id":123}""")]
    public async Task AnInvalidWorkflowIdIsAnError(string args)
    {
        using var f = new Fixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Contains("workflow_id", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnUnknownWorkflowIsAnError()
    {
        using var f = new Fixture();

        Assert.Equal("workflow not found", (await Run(f, Guid.NewGuid())).GetProperty("error").GetString());
    }

    // A workflow with no tests passes vacuously, but says so — otherwise
    // "all_passed" would read as evidence it doesn't have.
    [Fact]
    public async Task AWorkflowWithoutTestsPassesButSaysSo()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await Run(f, id);

        Assert.Equal(0, result.GetProperty("test_count").GetInt32());
        Assert.True(result.GetProperty("all_passed").GetBoolean());
        Assert.Contains("no acceptance tests", result.GetProperty("note").GetString());
    }

    // ─── the 24-hour evidence window ────────────────────────────────────

    // No run at all is "no evidence", which must NOT read as a pass.
    [Fact]
    public async Task ATestWithNoMatchingRunGradesAsError()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id);

        var result = await Run(f, id);

        Assert.False(result.GetProperty("all_passed").GetBoolean());
        var graded = ResultFor(result, "happy path");
        Assert.Equal(WorkflowAcceptanceTestStatus.Error, graded.GetProperty("status").GetString());
        Assert.Equal("no_matching_run", graded.GetProperty("reason").GetString());
    }

    // A run from before the window describes a possibly-different DAG, so it
    // is not evidence.
    [Fact]
    public async Task ARunOlderThanTheWindowIsNotEvidence()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id);
        f.SeedRun(id, completedAt: DateTime.UtcNow.AddHours(-25));

        var result = await Run(f, id);

        Assert.Equal(WorkflowAcceptanceTestStatus.Error,
            ResultFor(result, "happy path").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARunInsideTheWindowIsEvidence()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id);
        f.SeedRun(id, completedAt: DateTime.UtcNow.AddHours(-23));

        var result = await Run(f, id);

        Assert.True(result.GetProperty("all_passed").GetBoolean());
    }

    // An unfinished or failed run is not a completed run.
    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Pending)]
    public async Task OnlyCompletedRunsAreCandidates(string status)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id);
        f.SeedRun(id, status: status);

        var result = await Run(f, id);

        Assert.Equal(WorkflowAcceptanceTestStatus.Error,
            ResultFor(result, "happy path").GetProperty("status").GetString());
    }

    // The inputs must match, or the test would be graded against a run that
    // exercised something else entirely.
    [Fact]
    public async Task ARunWithDifferentInputsIsNotAMatch()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, inputs: """{"host":"rtr-1"}""");
        f.SeedRun(id, input: """{"host":"rtr-2"}""");

        var result = await Run(f, id);

        Assert.Equal(WorkflowAcceptanceTestStatus.Error,
            ResultFor(result, "happy path").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARunWhosePinnedInputsMatchIsUsed()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, inputs: """{"host":"rtr-1"}""");
        f.SeedRun(id, input: """{"host":"rtr-1","extra":true}""");

        Assert.True((await Run(f, id)).GetProperty("all_passed").GetBoolean());
    }

    // ─── grading ────────────────────────────────────────────────────────

    [Fact]
    public async Task ATestWhoseAssertionsHoldPasses()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, assertions: """[{"kind":"status_equals","expected":"completed"}]""");
        var runId = f.SeedRun(id);

        var result = await Run(f, id);

        Assert.True(result.GetProperty("all_passed").GetBoolean());
        var graded = ResultFor(result, "happy path");
        Assert.Equal(WorkflowAcceptanceTestStatus.Passed, graded.GetProperty("status").GetString());
        Assert.Equal(runId, graded.GetProperty("run_id").GetGuid());
    }

    // A failure carries the details so the agent can act rather than re-run
    // blindly.
    [Fact]
    public async Task AFailingTestReportsItsFailures()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, assertions: """[{"kind":"status_equals","expected":"failed"}]""");
        f.SeedRun(id);

        var result = await Run(f, id);

        Assert.False(result.GetProperty("all_passed").GetBoolean());
        var graded = ResultFor(result, "happy path");
        Assert.Equal(WorkflowAcceptanceTestStatus.Failed, graded.GetProperty("status").GetString());
        Assert.NotEmpty(graded.GetProperty("failures").EnumerateArray());
    }

    // Step-level assertions read the run's step outputs.
    [Fact]
    public async Task StepAssertionsAreGradedAgainstTheRunsSteps()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, assertions: """
            [{"kind":"step_succeeded","path":"n1"},
             {"kind":"step_output_contains","path":"n1.stdout","expected":"IOS"}]
            """);
        var runId = f.SeedRun(id);
        f.SeedStep(runId, "n1", StepStatus.Completed, """{"stdout":"Cisco IOS 15.2"}""");

        Assert.True((await Run(f, id)).GetProperty("all_passed").GetBoolean());
    }

    [Fact]
    public async Task AMissingStepFailsTheTest()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, assertions: """[{"kind":"step_succeeded","path":"ghost"}]""");
        var runId = f.SeedRun(id);
        f.SeedStep(runId, "n1");

        Assert.False((await Run(f, id)).GetProperty("all_passed").GetBoolean());
    }

    // One failing test drags the whole verdict down — that is what makes
    // all_passed usable as a promotion gate.
    [Fact]
    public async Task OneFailureSinksTheOverallVerdict()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedTest(id, name: "good", assertions: """[{"kind":"status_equals","expected":"completed"}]""");
        f.SeedTest(id, name: "bad", assertions: """[{"kind":"status_equals","expected":"failed"}]""");
        f.SeedRun(id);

        var result = await Run(f, id);

        Assert.Equal(2, result.GetProperty("test_count").GetInt32());
        Assert.False(result.GetProperty("all_passed").GetBoolean());
        Assert.Equal(WorkflowAcceptanceTestStatus.Passed, ResultFor(result, "good").GetProperty("status").GetString());
        Assert.Equal(WorkflowAcceptanceTestStatus.Failed, ResultFor(result, "bad").GetProperty("status").GetString());
    }

    // ─── the verdict is persisted ───────────────────────────────────────

    // The stored verdict is what the checklist and the promotion gate read
    // later, so it has to survive the call.
    [Fact]
    public async Task ThePassVerdictIsPersistedOnTheTestRow()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var testId = f.SeedTest(id, assertions: """[{"kind":"status_equals","expected":"completed"}]""");
        var runId = f.SeedRun(id);

        await Run(f, id);

        var row = await f.Db.Set<WorkflowAcceptanceTest>().SingleAsync(t => t.WorkflowAcceptanceTestId == testId);
        Assert.Equal(WorkflowAcceptanceTestStatus.Passed, row.LastStatus);
        Assert.Equal(runId, row.LastRunId);
        Assert.NotNull(row.LastRunAt);
        Assert.Empty(row.LastFailures.EnumerateArray());
    }

    [Fact]
    public async Task TheFailureDetailIsPersisted()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var testId = f.SeedTest(id, assertions: """[{"kind":"status_equals","expected":"failed"}]""");
        f.SeedRun(id);

        await Run(f, id);

        var row = await f.Db.Set<WorkflowAcceptanceTest>().SingleAsync(t => t.WorkflowAcceptanceTestId == testId);
        Assert.Equal(WorkflowAcceptanceTestStatus.Failed, row.LastStatus);
        Assert.NotEmpty(row.LastFailures.EnumerateArray());
    }

    // The "no evidence" verdict is persisted too, with a hint on how to fix it.
    [Fact]
    public async Task TheNoRunVerdictIsPersistedWithAHint()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var testId = f.SeedTest(id);

        await Run(f, id);

        var row = await f.Db.Set<WorkflowAcceptanceTest>().SingleAsync(t => t.WorkflowAcceptanceTestId == testId);
        Assert.Equal(WorkflowAcceptanceTestStatus.Error, row.LastStatus);
        Assert.Null(row.LastRunId);
        Assert.Contains("run_workflow", row.LastFailures[0].GetProperty("hint").GetString());
    }

    // Re-grading against fresh evidence must overwrite the old verdict, not
    // leave a stale failure behind.
    [Fact]
    public async Task RegradingOverwritesAStaleVerdict()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var testId = f.SeedTest(id, assertions: """[{"kind":"status_equals","expected":"completed"}]""");
        await Run(f, id);   // no run yet → error
        Assert.Equal(WorkflowAcceptanceTestStatus.Error,
            (await f.Db.Set<WorkflowAcceptanceTest>().SingleAsync()).LastStatus);

        f.SeedRun(id);
        await Run(f, id);

        var row = await f.Db.Set<WorkflowAcceptanceTest>().SingleAsync(t => t.WorkflowAcceptanceTestId == testId);
        Assert.Equal(WorkflowAcceptanceTestStatus.Passed, row.LastStatus);
        Assert.Empty(row.LastFailures.EnumerateArray());
    }

    // ─── isolation ──────────────────────────────────────────────────────

    // Tests belong to one workflow; another workflow's runs are not evidence.
    [Fact]
    public async Task AnotherWorkflowsRunIsNotEvidence()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var otherId = f.SeedWorkflow();
        f.SeedTest(id);
        f.SeedRun(otherId);

        Assert.Equal(WorkflowAcceptanceTestStatus.Error,
            ResultFor(await Run(f, id), "happy path").GetProperty("status").GetString());
    }

    [Fact]
    public async Task OnlyTheTargetWorkflowsTestsAreGraded()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var otherId = f.SeedWorkflow();
        f.SeedTest(id, name: "mine");
        f.SeedTest(otherId, name: "theirs");
        f.SeedRun(id);

        var result = await Run(f, id);

        Assert.Equal(1, result.GetProperty("test_count").GetInt32());
        Assert.Equal("mine", Assert.Single(result.GetProperty("results").EnumerateArray())
            .GetProperty("name").GetString());
    }

    [Fact]
    public void HandlerAdvertisesItsName()
    {
        using var f = new Fixture();

        Assert.Equal("run_acceptance_tests", f.Build().Name);
    }
}
