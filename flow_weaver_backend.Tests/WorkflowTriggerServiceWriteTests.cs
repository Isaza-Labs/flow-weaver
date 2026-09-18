using System.Text;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowTriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.Tests;

// Trigger creation. Two contracts matter: a webhook trigger auto-generates a
// signing secret so the PUBLIC ingest is authenticated by default (the
// plaintext is revealed exactly once, like an API key), and a schedule trigger
// gets its NextRunAt stamped at write time — without it the scheduler's
// due-query never picks the trigger up and it silently never fires.
public class WorkflowTriggerServiceWriteTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public FakeUser Caller { get; set; } = new();

        public WorkflowTriggerService Build() => new(
            new WorkflowTriggerRepository(Db),
            new RepositoryBase<WorkflowModel>(Db),
            Caller,
            new AllowAllEffectivePermissions(),
            new FakeCrypto(),
            new FakeAudit(), NullLogger<WorkflowTriggerService>.Instance);

        public Guid SeedWorkflow()
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "wf",
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedTrigger(
            Guid workflowId, string type = WorkflowTriggerModel.TypeWebhook,
            string? secret = null)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowTriggerModel>().Add(new WorkflowTriggerModel
            {
                WorkflowTriggerId = id,
                WorkflowId = workflowId,
                Name = "hook",
                Type = type,
                EncryptedSecret = secret is null ? null : Encoding.UTF8.GetBytes(secret),
                Enabled = true,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static CreateWorkflowTrigger Create(
        string name = "hook", string type = "webhook",
        string? cron = null, string? timezone = null,
        bool? allowUnsigned = null, bool? enabled = null)
        => new()
        {
            Name = name,
            Type = type,
            CronExpression = cron,
            Timezone = timezone,
            AllowUnsigned = allowUnsigned,
            Enabled = enabled,
        };

    private static WorkflowTriggerResponse Ok(ActionResult<WorkflowTriggerResponse> result)
    {
        if (result.Result is CreatedAtActionResult created)
            return Assert.IsType<WorkflowTriggerResponse>(created.Value);
        if (result.Result is ObjectResult obj && obj.StatusCode is 200 or 201)
            return Assert.IsType<WorkflowTriggerResponse>(obj.Value);
        Assert.Null(result.Result);
        return Assert.IsType<WorkflowTriggerResponse>(result.Value);
    }

    // ─── validation ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingNameIsRejected(string name)
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(workflowId, Create(name: name));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(f.Db.Set<WorkflowTriggerModel>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingTypeIsRejected(string type)
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        Assert.IsType<BadRequestObjectResult>(
            (await f.Build().PostForWorkflowAsync(workflowId, Create(type: type))).Result);
    }

    // The parent workflow must exist, or a trigger could be hung off nothing.
    [Fact]
    public async Task UnknownParentWorkflowIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>(
            (await f.Build().PostForWorkflowAsync(Guid.NewGuid(), Create())).Result);
    }

    // ─── schedule triggers ──────────────────────────────────────────────

    [Theory]
    [InlineData("cron")]
    [InlineData("schedule")]
    [InlineData("CRON")]
    public async Task ScheduleTriggersRequireACronExpression(string type)
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(workflowId, Create(type: type, cron: null));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("cron_expression is required", bad.Value!.ToString());
    }

    [Fact]
    public async Task InvalidCronIsRejectedWithTheParserMessage()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().PostForWorkflowAsync(
            workflowId, Create(type: "cron", cron: "not a cron"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(f.Db.Set<WorkflowTriggerModel>());
    }

    // Without NextRunAt the scheduler's due-query skips the row and the
    // trigger silently never fires.
    [Fact]
    public async Task ScheduleTriggerIsStampedWithItsFirstFireTime()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        await f.Build().PostForWorkflowAsync(workflowId, Create(type: "cron", cron: "0 3 * * *"));

        var row = await f.Db.Set<WorkflowTriggerModel>().SingleAsync();
        Assert.NotNull(row.NextRunAt);
        Assert.True(row.NextRunAt > DateTime.UtcNow);
    }

    // A disabled schedule carries no NextRunAt, so it stays out of the query.
    [Fact]
    public async Task DisabledScheduleCarriesNoNextRun()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        await f.Build().PostForWorkflowAsync(
            workflowId, Create(type: "cron", cron: "0 3 * * *", enabled: false));

        Assert.Null((await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).NextRunAt);
    }

    // Webhook triggers are event-driven, so they never carry a fire time.
    [Fact]
    public async Task WebhookTriggerCarriesNoNextRun()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        await f.Build().PostForWorkflowAsync(workflowId, Create(type: "webhook"));

        Assert.Null((await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).NextRunAt);
    }

    [Fact]
    public async Task TimezoneDefaultsWhenOmitted()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        await f.Build().PostForWorkflowAsync(workflowId, Create(type: "cron", cron: "0 3 * * *"));

        Assert.False(string.IsNullOrWhiteSpace(
            (await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).Timezone));
    }

    [Fact]
    public async Task ExplicitTimezoneIsHonoured()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        await f.Build().PostForWorkflowAsync(
            workflowId, Create(type: "cron", cron: "0 3 * * *", timezone: "Europe/Madrid"));

        Assert.Equal("Europe/Madrid", (await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).Timezone);
    }

    // ─── webhook secret (authenticated by default) ──────────────────────

    // The public ingest must be signed by default — a webhook created without
    // a secret would accept anything.
    [Fact]
    public async Task WebhookTriggerAutoGeneratesASigningSecret()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var body = Ok(await f.Build().PostForWorkflowAsync(workflowId, Create(type: "webhook")));

        Assert.False(string.IsNullOrWhiteSpace(body.WebhookSecret));
        Assert.Equal(64, body.WebhookSecret!.Length);   // 32 bytes as hex
        Assert.NotNull((await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).EncryptedSecret);
    }

    // Hex only — no characters the sender has to escape in its config.
    [Fact]
    public async Task TheGeneratedSecretIsLowercaseHex()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var body = Ok(await f.Build().PostForWorkflowAsync(workflowId, Create(type: "webhook")));

        Assert.All(body.WebhookSecret!, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), $"unexpected char '{c}'"));
    }

    [Fact]
    public async Task GeneratedSecretsAreUnique()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var svc = f.Build();

        var first = Ok(await svc.PostForWorkflowAsync(workflowId, Create(type: "webhook")));
        var second = Ok(await svc.PostForWorkflowAsync(workflowId, Create(type: "webhook")));

        Assert.NotEqual(first.WebhookSecret, second.WebhookSecret);
    }

    // The explicit opt-out skips generation.
    [Fact]
    public async Task AllowUnsignedSkipsSecretGeneration()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var body = Ok(await f.Build().PostForWorkflowAsync(
            workflowId, Create(type: "webhook", allowUnsigned: true)));

        Assert.Null(body.WebhookSecret);
        Assert.Null((await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).EncryptedSecret);
    }

    // Non-webhook triggers have no ingest to authenticate.
    [Fact]
    public async Task NonWebhookTriggersGetNoSecret()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();

        var body = Ok(await f.Build().PostForWorkflowAsync(
            workflowId, Create(type: "cron", cron: "0 3 * * *")));

        Assert.Null(body.WebhookSecret);
    }

    // One-time reveal: the plaintext never comes back on a subsequent read.
    [Fact]
    public async Task TheSecretIsRevealedOnlyOnce()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var created = Ok(await f.Build().PostForWorkflowAsync(workflowId, Create(type: "webhook")));

        var reread = Ok(await f.Build().GetByIdAsync(created.WorkflowTriggerId));

        Assert.NotNull(created.WebhookSecret);
        Assert.Null(reread.WebhookSecret);
    }

    // ─── secret rotation ────────────────────────────────────────────────

    [Fact]
    public async Task RotateReturnsANewSecretAndStoresIt()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var triggerId = f.SeedTrigger(workflowId, secret: "old-secret");

        var body = Ok(await f.Build().RotateWebhookSecretAsync(triggerId));

        Assert.False(string.IsNullOrWhiteSpace(body.WebhookSecret));
        var stored = Encoding.UTF8.GetString(
            (await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).EncryptedSecret!);
        Assert.Equal(body.WebhookSecret, stored);
        Assert.NotEqual("old-secret", stored);
    }

    // Rotating in a secret means "now require signatures" — leaving
    // AllowUnsigned on would make the rotation pointless.
    [Fact]
    public async Task RotateAlsoTurnsOffUnsignedDeliveries()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var triggerId = f.SeedTrigger(workflowId);
        var trigger = await f.Db.Set<WorkflowTriggerModel>().SingleAsync();
        trigger.AllowUnsigned = true;
        await f.Db.SaveChangesAsync();

        await f.Build().RotateWebhookSecretAsync(triggerId);

        Assert.False((await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).AllowUnsigned);
    }

    [Fact]
    public async Task RotateOnANonWebhookIsRejected()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var triggerId = f.SeedTrigger(workflowId, type: "cron");

        var result = await f.Build().RotateWebhookSecretAsync(triggerId);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("only webhook triggers", bad.Value!.ToString());
    }

    [Fact]
    public async Task RotateUnknownTriggerIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>(
            (await f.Build().RotateWebhookSecretAsync(Guid.NewGuid())).Result);
    }

    // ─── read + delete ──────────────────────────────────────────────────

    [Fact]
    public async Task GetByWorkflowReturnsOnlyThatWorkflowsTriggers()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var otherWorkflowId = f.SeedWorkflow();
        f.SeedTrigger(workflowId);
        f.SeedTrigger(otherWorkflowId);

        var ok = Assert.IsType<OkObjectResult>((await f.Build().GetByWorkflowAsync(workflowId)).Result);
        var body = Assert.IsType<ListResponse<WorkflowTriggerResponse>>(ok.Value);

        Assert.Equal(1, body.Total);
    }

    [Fact]
    public async Task GetByIdUnknownIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task DeleteIsASoftDelete()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var triggerId = f.SeedTrigger(workflowId);

        await f.Build().DeleteAsync(triggerId);

        Assert.False((await f.Db.Set<WorkflowTriggerModel>().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task DeleteUnknownIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().DeleteAsync(Guid.NewGuid())).Result);
    }

    // ─── update ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAppliesSuppliedFieldsOnly()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var triggerId = f.SeedTrigger(workflowId);

        var body = Ok(await f.Build().UpdateAsync(triggerId, new UpdateWorkflowTrigger { Name = "renamed" }));

        Assert.Equal("renamed", body.Name);
        Assert.Equal(WorkflowTriggerModel.TypeWebhook, body.Type);
    }

    [Fact]
    public async Task UpdateUnknownIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>(
            (await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateWorkflowTrigger())).Result);
    }
    // ─── allow_unsigned is admin-only to switch on ─────────────────────

    // An unsigned webhook is an unauthenticated endpoint that starts runs;
    // trigger.manage alone (Operator tier) must not be enough to open it.
    [Fact]
    public async Task AnOperatorCannotCreateAnUnsignedWebhook()
    {
        using var f = new Fixture();
        f.Caller = new FakeUser { Roles = new[] { "operator" } };
        var wf = f.SeedWorkflow();

        var result = (await f.Build().PostForWorkflowAsync(wf, Create(allowUnsigned: true))).Result;

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(f.Db.Set<WorkflowTriggerModel>());
    }

    [Fact]
    public async Task AnOperatorCanStillCreateASignedWebhook()
    {
        using var f = new Fixture();
        f.Caller = new FakeUser { Roles = new[] { "operator" } };
        var wf = f.SeedWorkflow();

        var result = (await f.Build().PostForWorkflowAsync(wf, Create(allowUnsigned: false))).Result;

        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task AnOperatorCannotSwitchAnExistingWebhookToUnsigned()
    {
        using var f = new Fixture();
        f.Caller = new FakeUser { Roles = new[] { "operator" } };
        var id = f.SeedTrigger(f.SeedWorkflow(), secret: "s");

        var result = (await f.Build().UpdateAsync(id, new UpdateWorkflowTrigger { AllowUnsigned = true })).Result;

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(f.Db.Set<WorkflowTriggerModel>().Single().AllowUnsigned);
    }

    // Turning it off only tightens security, and editing other fields of a
    // trigger an admin already made unsigned must keep working.
    [Fact]
    public async Task AnOperatorCanSwitchUnsignedOffAndEditAnUnsignedTrigger()
    {
        using var f = new Fixture();
        f.Caller = new FakeUser { Roles = new[] { "operator" } };
        var id = f.SeedTrigger(f.SeedWorkflow());
        var row = f.Db.Set<WorkflowTriggerModel>().Single();
        row.AllowUnsigned = true;
        f.Db.SaveChanges();

        Assert.Equal("renamed", Ok(await f.Build().UpdateAsync(
            id, new UpdateWorkflowTrigger { Name = "renamed", AllowUnsigned = true })).Name);

        Assert.False(Ok(await f.Build().UpdateAsync(
            id, new UpdateWorkflowTrigger { AllowUnsigned = false })).AllowUnsigned);
        Assert.False(f.Db.Set<WorkflowTriggerModel>().Single().AllowUnsigned);
    }

    [Fact]
    public async Task AnAdminCanCreateAnUnsignedWebhook()
    {
        using var f = new Fixture();
        var wf = f.SeedWorkflow();

        var result = (await f.Build().PostForWorkflowAsync(wf, Create(allowUnsigned: true))).Result;

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.True(f.Db.Set<WorkflowTriggerModel>().Single().AllowUnsigned);
    }
}
