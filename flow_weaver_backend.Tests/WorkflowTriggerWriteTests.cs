using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowTriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.Tests;

// Workflow triggers: the scheduled and webhook entry points that fire a run
// without anyone clicking anything.
//
// The cron half is what carries risk. `NextRunAt` is what the scheduler's
// due-query reads, so a trigger that saves a bad expression — or keeps a stale
// NextRunAt after being disabled — either never fires or fires forever.
public class WorkflowTriggerWriteTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public WorkflowTriggerService Build() => new(
            new WorkflowTriggerRepository(Db),
            new RepositoryBase<WorkflowModel>(Db),
            new FakeUser(),
            new AllowAllEffectivePermissions(),
            new FakeCrypto(),
            new FakeAudit(), NullLogger<WorkflowTriggerService>.Instance);

        public Guid SeedWorkflow()
        {
            var id = Guid.NewGuid();
            Db.Workflows.Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "wf",
                Environment = "draft",
                Version = 1,
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedTrigger(
            Guid workflowId, string type = "schedule", string? cron = "0 * * * *",
            bool enabled = true, DateTime? nextRunAt = null)
        {
            var id = Guid.NewGuid();
            Db.WorkflowTriggers.Add(new WorkflowTriggerModel
            {
                WorkflowTriggerId = id,
                WorkflowId = workflowId,
                Name = "nightly",
                Type = type,
                CronExpression = cron,
                Timezone = "UTC",
                InputSchema = TestJson.Element("{}"),
                InputDefaults = TestJson.Element("{}"),
                Enabled = enabled,
                NextRunAt = nextRunAt,
                TargetDevices = new List<Guid>(),
                NotifyOn = new List<string>(),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string ErrorOf<T>(ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    private static WorkflowTriggerResponse Body(ActionResult<WorkflowTriggerResponse> result)
        => result.Value ?? Assert.IsType<WorkflowTriggerResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    private static CreateWorkflowTrigger New(
        string name = "nightly", string type = "schedule", string? cron = "0 * * * *")
        => new() { Name = name, Type = type, CronExpression = cron, Timezone = "UTC" };

    // ─── Create ─────────────────────────────────────────────────────────

    // A trigger only makes sense attached to a workflow, so the flat POST is
    // refused with the route the caller should use instead.
    [Fact]
    public async Task Create_TheFlatRouteRedirectsToTheNestedOne()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New());

        Assert.Contains("/api/workflow/{workflowId}/triggers", ErrorOf(result));
    }

    [Theory]
    [InlineData("", "schedule", "name is required")]
    [InlineData("nightly", "", "type is required")]
    public async Task Create_RequiresANameAndAType(string name, string type, string expected)
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(workflowId, New(name, type));

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(f.Db.WorkflowTriggers);
    }

    // A schedule with no cron would never fire; catching it at save time is
    // the only chance before someone waits a week for a run that never comes.
    [Theory]
    [InlineData("schedule")]
    [InlineData("cron")]
    public async Task Create_AScheduleWithoutACronIsRejected(string type)
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(workflowId, New(type: type, cron: null));

        Assert.Contains("cron_expression is required", ErrorOf(result));
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("99 * * * *")]
    [InlineData("* * *")]
    public async Task Create_AnInvalidCronIsRejectedWithTheParserReason(string cron)
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(workflowId, New(cron: cron));

        Assert.NotEmpty(ErrorOf(result));
        Assert.Empty(f.Db.WorkflowTriggers);
    }

    [Fact]
    public async Task Create_AnUnknownWorkflowIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().PostForWorkflowAsync(Guid.NewGuid(), New());

        Assert.Contains("workflow not found", ErrorOf(result));
    }

    // A valid schedule is anchored immediately — NextRunAt is what the
    // scheduler's due-query reads.
    [Fact]
    public async Task Create_AValidScheduleIsAnchored()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(workflowId, New(cron: "0 * * * *"));

        Assert.NotNull(Body(result));
        var saved = Assert.Single(f.Db.WorkflowTriggers);
        Assert.Equal(workflowId, saved.WorkflowId);
        Assert.NotNull(saved.NextRunAt);
        Assert.True(saved.NextRunAt > DateTime.UtcNow);
    }

    // A webhook trigger has no cron at all — requiring one would make the
    // whole webhook path unusable.
    [Fact]
    public async Task Create_AWebhookTriggerNeedsNoCron()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(
            workflowId, New(type: WorkflowTriggerModel.TypeWebhook, cron: null));

        Assert.NotNull(Body(result));
        Assert.Null(Assert.Single(f.Db.WorkflowTriggers).NextRunAt);
    }

    // ─── List ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsTheWorkflowsTriggers()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        f.SeedTrigger(workflowId);

        var result = await f.Build().GetByWorkflowAsync(workflowId);

        var page = Assert.IsType<ListResponse<WorkflowTriggerResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task List_AnUnknownWorkflowIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().GetByWorkflowAsync(Guid.NewGuid());

        Assert.Contains("workflow not found", ErrorOf(result));
    }

    [Fact]
    public async Task List_OnlyReturnsTheAskedWorkflowsTriggers()
    {
        using var f = new Fixture();
        var target = f.SeedWorkflow();
        var other = f.SeedWorkflow();
        f.SeedTrigger(target);
        f.SeedTrigger(other);

        var result = await f.Build().GetByWorkflowAsync(target);

        var page = Assert.IsType<ListResponse<WorkflowTriggerResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
    }

    // ─── Update ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateWorkflowTrigger { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_OmittedFieldsAreLeftUntouched()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var id = f.SeedTrigger(workflowId, cron: "0 * * * *");

        await f.Build().UpdateAsync(id, new UpdateWorkflowTrigger { Description = "runs hourly" });

        var saved = f.Db.WorkflowTriggers.Single();
        Assert.Equal("nightly", saved.Name);
        Assert.Equal("0 * * * *", saved.CronExpression);
        Assert.Equal("runs hourly", saved.Description);
    }

    [Fact]
    public async Task Update_AnInvalidCronIsRejectedAndNothingIsSaved()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var id = f.SeedTrigger(workflowId, cron: "0 * * * *");

        var result = await f.Build().UpdateAsync(
            id, new UpdateWorkflowTrigger { CronExpression = "not a cron" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("0 * * * *", f.Db.WorkflowTriggers.Single().CronExpression);
    }

    // Every update re-anchors the schedule, which is what re-activates a
    // trigger that was toggled back on.
    [Fact]
    public async Task Update_ReAnchorsTheScheduleOnEveryEdit()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var stale = DateTime.UtcNow.AddYears(-1);
        var id = f.SeedTrigger(workflowId, nextRunAt: stale);

        await f.Build().UpdateAsync(id, new UpdateWorkflowTrigger { Description = "touched" });

        Assert.True(f.Db.WorkflowTriggers.Single().NextRunAt > DateTime.UtcNow);
    }

    // Disabling must clear the anchor, or the scheduler keeps seeing the
    // trigger as due.
    [Fact]
    public async Task Update_DisablingClearsTheAnchor()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var id = f.SeedTrigger(workflowId, nextRunAt: DateTime.UtcNow.AddHours(1));

        await f.Build().UpdateAsync(id, new UpdateWorkflowTrigger { Enabled = false });

        var saved = f.Db.WorkflowTriggers.Single();
        Assert.False(saved.Enabled);
        Assert.Null(saved.NextRunAt);
    }

    // Switching a schedule to a webhook must also clear the anchor — the
    // scheduler has no business firing a webhook trigger.
    [Fact]
    public async Task Update_SwitchingAwayFromAScheduleClearsTheAnchor()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var id = f.SeedTrigger(workflowId, nextRunAt: DateTime.UtcNow.AddHours(1));

        await f.Build().UpdateAsync(
            id, new UpdateWorkflowTrigger { Type = WorkflowTriggerModel.TypeWebhook });

        Assert.Null(f.Db.WorkflowTriggers.Single().NextRunAt);
    }

    [Fact]
    public async Task Update_ChangingTheCronReAnchorsToTheNewSchedule()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var id = f.SeedTrigger(workflowId, cron: "0 3 * * *");

        await f.Build().UpdateAsync(id, new UpdateWorkflowTrigger { CronExpression = "*/5 * * * *" });

        var saved = f.Db.WorkflowTriggers.Single();
        Assert.Equal("*/5 * * * *", saved.CronExpression);
        Assert.NotNull(saved.NextRunAt);
    }

    [Fact]
    public async Task Update_TargetDevicesAndNotificationSettingsArePersisted()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var id = f.SeedTrigger(workflowId);
        var device = Guid.NewGuid();

        await f.Build().UpdateAsync(id, new UpdateWorkflowTrigger
        {
            TargetDevices = new List<Guid> { device },
            NotifyOn = new List<string> { "failure" },
            NotificationWebhookURL = "https://hooks.test/notify",
        });

        var saved = f.Db.WorkflowTriggers.Single();
        Assert.Equal(device, Assert.Single(saved.TargetDevices));
        Assert.Equal("failure", Assert.Single(saved.NotifyOn));
        Assert.Equal("https://hooks.test/notify", saved.NotificationWebhookURL);
    }
}
