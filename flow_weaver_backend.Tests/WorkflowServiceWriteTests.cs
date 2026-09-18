using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Validation;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The write paths of WorkflowService: create, update and delete. Each runs a
// gauntlet — schema, references, vendor catalog, corporate policy, per-resource
// grants — and the ORDER matters: a blocked write must leave nothing behind.
// These tests drive each gate independently with scriptable validators.
public class WorkflowServiceWriteTests
{

    private sealed class ScriptedSchemaValidator : IWorkflowSchemaValidator
    {
        public WorkflowValidationResult Result { get; set; } = WorkflowValidationResult.Ok();
        public string CurrentSchemaVersion => "v1";
        public string RawJson => "{}";
        public int Calls { get; private set; }
        public WorkflowValidationResult Validate(JsonElement nodes, JsonElement edges)
        {
            Calls++;
            return Result;
        }
    }

    private sealed class ScriptedReferenceValidator : IWorkflowReferenceValidator
    {
        public WorkflowValidationResult Result { get; set; } = WorkflowValidationResult.Ok();
        public string? NameSeen { get; private set; }
        public string? DescriptionSeen { get; private set; }
        public Task<WorkflowValidationResult> ValidateAsync(
            JsonElement nodes, CancellationToken ct, JsonElement? previousNodes = null)
            => Task.FromResult(Result);
        public JsonElement? PreviousNodesSeen { get; private set; }
        public Task<WorkflowValidationResult> ValidateWithContextAsync(
            JsonElement nodes, string? workflowName, string? workflowDescription, CancellationToken ct,
            JsonElement? previousNodes = null)
        {
            NameSeen = workflowName;
            DescriptionSeen = workflowDescription;
            PreviousNodesSeen = previousNodes;
            return Task.FromResult(Result);
        }
    }

    private sealed class ScriptedVendorValidator : IVendorCommandValidator
    {
        public WorkflowValidationResult Result { get; set; } = WorkflowValidationResult.Ok();
        public Task<WorkflowValidationResult> ValidateAsync(
            JsonElement nodes, IReadOnlyCollection<Guid> targetDeviceIds, CancellationToken ct)
            => Task.FromResult(Result);
        public Task<IReadOnlyList<string>> ValidateCommandsAsync(
            string deviceType, IReadOnlyList<string> commands, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class ScriptedPolicyEvaluator : IPolicyEvaluator
    {
        public PolicyDecision Decision { get; set; } = new(true, null, null);
        public List<string> ActionsSeen { get; } = new();
        public Task<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken ct)
        {
            ActionsSeen.Add(context.Action);
            return Task.FromResult(Decision);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ScriptedSchemaValidator Schema { get; } = new();
        public ScriptedReferenceValidator References { get; } = new();
        public ScriptedVendorValidator Vendor { get; } = new();
        public ScriptedPolicyEvaluator Policy { get; } = new();
        public FakeUser Caller { get; set; } = new();

        public WorkflowService Build() => new(
            new RepositoryBase<WorkflowModel>(Db),
            Caller,
            Schema,
            References,
            Vendor,
            new FakeAudit(),
            new FakeTrace(),
            Policy,
            new ResourcePermissionService(
                new ResourcePermissionRepository(Db), new UserRepository(Db),
                Caller, new FakeAudit(), NullLogger<ResourcePermissionService>.Instance),
            new FakeAppSettings(),
            NullLogger<WorkflowService>.Instance);

        public Guid SeedWorkflow(
            string name = "wf", string environment = "draft",
            int version = 1, Guid? lastSimulationId = null)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = name,
                Description = "original description",
                Version = version,
                Environment = environment,
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                LastSimulationId = lastSimulationId,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static int StatusOf<T>(ActionResult<T> result)
        => Assert.IsType<ObjectResult>(result.Result).StatusCode!.Value;

    private static WorkflowResponse Created(ActionResult<WorkflowResponse> result)
        => Assert.IsType<WorkflowResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);

    private static WorkflowResponse Ok(ActionResult<WorkflowResponse> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<WorkflowResponse>(result.Value);
    }

    private static CreateWorkflow NewWorkflow(
        string name = "deploy", string? description = "does a deploy",
        string nodes = "[]", string edges = "[]")
        => new()
        {
            Name = name,
            Description = description,
            Nodes = TestJson.Element(nodes),
            Edges = TestJson.Element(edges),
        };

    // ─── create: validation gauntlet ────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_MissingName_Is400(string name)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(NewWorkflow(name: name));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(f.Db.Set<WorkflowModel>());
    }

    [Fact]
    public async Task Create_SchemaInvalid_Is400AndPersistsNothing()
    {
        using var f = new Fixture();
        f.Schema.Result = WorkflowValidationResult.Invalid(new[] { "node 'x' has no type" });

        var result = await f.Build().PostAsync(NewWorkflow());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("schema_invalid", bad.Value!.ToString());
        Assert.Empty(f.Db.Set<WorkflowModel>());
    }

    // Reference validation is what catches agent hallucinations (placeholder
    // or fabricated ids) before an un-runnable workflow is persisted.
    [Fact]
    public async Task Create_ReferencesInvalid_Is400AndPersistsNothing()
    {
        using var f = new Fixture();
        f.References.Result = WorkflowValidationResult.Invalid(new[] { "integration_id not found" });

        var result = await f.Build().PostAsync(NewWorkflow());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("references_invalid", bad.Value!.ToString());
        Assert.Empty(f.Db.Set<WorkflowModel>());
    }

    // A deny-rule must stop the write at the boundary, not leave a half-created
    // workflow behind.
    [Fact]
    public async Task Create_PolicyBlocked_Is403AndPersistsNothing()
    {
        using var f = new Fixture();
        f.Policy.Decision = new PolicyDecision(false, "no-ssh-in-draft", "ssh is not allowed here");

        var result = await f.Build().PostAsync(NewWorkflow());

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(f.Db.Set<WorkflowModel>());
    }

    [Fact]
    public async Task Create_PolicyIsEvaluatedForTheCreateAction()
    {
        using var f = new Fixture();

        await f.Build().PostAsync(NewWorkflow());

        Assert.Equal("create", Assert.Single(f.Policy.ActionsSeen));
    }

    // ─── create: success ────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsWithDefaults()
    {
        using var f = new Fixture();

        var body = Created(await f.Build().PostAsync(NewWorkflow()));

        Assert.Equal("deploy", body.Name);
        var row = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.Equal(1, row.Version);
        Assert.Equal("draft", row.Environment);
        Assert.Equal("v1", row.SchemaVersion);
        Assert.True(row.IsActive);
    }

    // Attribution: every created workflow records who created it — this
    // path serves both the UI and the agent's chat tools, and it used to
    // leave created_by null.
    [Fact]
    public async Task Create_StampsCreatedByFromCaller()
    {
        using var f = new Fixture();

        await f.Build().PostAsync(NewWorkflow());

        var row = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.Equal("tester", row.CreatedBy);
    }

    // Missing optional JSON payloads must become empty structures, not
    // Undefined elements that blow up later.
    [Fact]
    public async Task Create_OmittedJsonFieldsBecomeEmptyStructures()
    {
        using var f = new Fixture();
        var dto = new CreateWorkflow { Name = "bare" };

        await f.Build().PostAsync(dto);

        var row = await f.Db.Set<WorkflowModel>().SingleAsync();
        Assert.Equal(JsonValueKind.Array, row.Nodes.ValueKind);
        Assert.Equal(JsonValueKind.Array, row.Edges.ValueKind);
        Assert.Equal(JsonValueKind.Object, row.Metadata.ValueKind);
        Assert.Equal(JsonValueKind.Object, row.InputSchema.ValueKind);
    }

    // The name/description reach the reference validator so it can flag
    // "description says email but there's no email node".
    [Fact]
    public async Task Create_PassesNameAndDescriptionToTheReferenceValidator()
    {
        using var f = new Fixture();

        await f.Build().PostAsync(NewWorkflow(name: "send report", description: "emails a PDF"));

        Assert.Equal("send report", f.References.NameSeen);
        Assert.Equal("emails a PDF", f.References.DescriptionSeen);
    }

    // Warnings are advisory — they ride along with a successful create.
    [Fact]
    public async Task Create_SurfacesWarningsFromBothValidators()
    {
        using var f = new Fixture();
        f.References.Result = WorkflowValidationResult.OkWithWarnings(new[] { "no email node" });
        f.Vendor.Result = WorkflowValidationResult.OkWithWarnings(new[] { "unknown command 'shw ver'" });

        var body = Created(await f.Build().PostAsync(NewWorkflow()));

        Assert.Equal(2, body.Warnings!.Count);
        Assert.Contains("no email node", body.Warnings);
        Assert.Contains("unknown command 'shw ver'", body.Warnings);
    }

    [Fact]
    public async Task Create_NoWarningsLeavesTheFieldUnset()
    {
        using var f = new Fixture();

        Assert.Null(Created(await f.Build().PostAsync(NewWorkflow())).Warnings);
    }

    // ─── update: guards ─────────────────────────────────────────────────

    [Fact]
    public async Task Update_UnknownWorkflow_Is404()
    {
        using var f = new Fixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateWorkflow { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // Production workflows are immutable — edits must go through promotion.
    [Fact]
    public async Task Update_ProductionWorkflow_Is409()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "production");

        var result = await f.Build().UpdateAsync(id, new UpdateWorkflow { Name = "renamed" });

        Assert.Equal(409, StatusOf(result));
        Assert.Equal("wf", (await f.Db.Set<WorkflowModel>().SingleAsync()).Name);
    }

    [Fact]
    public async Task Update_SchemaInvalid_Is400()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.Schema.Result = WorkflowValidationResult.Invalid(new[] { "bad node" });

        var result = await f.Build().UpdateAsync(
            id, new UpdateWorkflow { Nodes = TestJson.Element("""[{"id":"a"}]""") });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ReferencesInvalid_Is400()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.References.Result = WorkflowValidationResult.Invalid(new[] { "missing action" });

        var result = await f.Build().UpdateAsync(
            id, new UpdateWorkflow { Nodes = TestJson.Element("""[{"id":"a"}]""") });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // A rule targeting ssh would be bypassable if an existing workflow could be
    // edited into an ssh one without re-evaluation.
    [Fact]
    public async Task Update_StructuralChangeIsPolicyChecked()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.Policy.Decision = new PolicyDecision(false, "no-ssh", "blocked");

        var result = await f.Build().UpdateAsync(
            id, new UpdateWorkflow { Nodes = TestJson.Element("""[{"id":"ssh"}]""") });

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("update", Assert.Single(f.Policy.ActionsSeen));
    }

    // A cosmetic rename is not a structural change, so it skips policy and
    // revalidation entirely.
    [Fact]
    public async Task Update_CosmeticRenameSkipsPolicyAndRevalidation()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflow { Name = "renamed" }));

        Assert.Equal("renamed", body.Name);
        Assert.Empty(f.Policy.ActionsSeen);
        Assert.Equal(0, f.Schema.Calls);
    }

    // ─── update: versioning + simulation invalidation ───────────────────

    // Any structural edit bumps the version...
    [Fact]
    public async Task Update_StructuralChangeBumpsTheVersion()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(version: 3);

        var body = Ok(await f.Build().UpdateAsync(
            id, new UpdateWorkflow { Nodes = TestJson.Element("""[{"id":"a"}]""") }));

        Assert.Equal(4, body.Version);
    }

    [Fact]
    public async Task Update_CosmeticChangeDoesNotBumpTheVersion()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(version: 3);

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflow { Description = "new text" }));

        Assert.Equal(3, body.Version);
    }

    // ...and invalidates the previous simulation, so promotion refuses the
    // workflow until it is re-simulated.
    [Fact]
    public async Task Update_StructuralChangeClearsTheSimulationPointer()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(lastSimulationId: Guid.NewGuid());

        await f.Build().UpdateAsync(id, new UpdateWorkflow { Nodes = TestJson.Element("[]") });

        Assert.Null((await f.Db.Set<WorkflowModel>().SingleAsync()).LastSimulationId);
    }

    [Fact]
    public async Task Update_CosmeticChangeKeepsTheSimulationPointer()
    {
        using var f = new Fixture();
        var simulationId = Guid.NewGuid();
        var id = f.SeedWorkflow(lastSimulationId: simulationId);

        await f.Build().UpdateAsync(id, new UpdateWorkflow { Name = "renamed" });

        Assert.Equal(simulationId, (await f.Db.Set<WorkflowModel>().SingleAsync()).LastSimulationId);
    }

    // input_schema counts as structural even though it isn't nodes/edges.
    [Fact]
    public async Task Update_InputSchemaChangeIsStructural()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(version: 1, lastSimulationId: Guid.NewGuid());

        var body = Ok(await f.Build().UpdateAsync(
            id, new UpdateWorkflow { InputSchema = TestJson.Element("""{"type":"object"}""") }));

        Assert.Equal(2, body.Version);
        Assert.Null((await f.Db.Set<WorkflowModel>().SingleAsync()).LastSimulationId);
    }

    // ─── update: partial semantics ──────────────────────────────────────

    // Fields not supplied must survive untouched.
    [Fact]
    public async Task Update_OmittedFieldsAreLeftAlone()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflow { Name = "renamed" }));

        Assert.Equal("renamed", body.Name);
        Assert.Equal("original description", body.Description);
    }

    [Fact]
    public async Task Update_EmptyPatchIsANoOp()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(version: 2);

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflow()));

        Assert.Equal("wf", body.Name);
        Assert.Equal(2, body.Version);
    }

    // The updated name/description are what reach the reference validator, so a
    // rename+restructure is validated against the NEW text.
    [Fact]
    public async Task Update_ValidatesAgainstTheNewNameAndDescription()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        await f.Build().UpdateAsync(id, new UpdateWorkflow
        {
            Name = "new name",
            Description = "new description",
            Nodes = TestJson.Element("[]"),
        });

        Assert.Equal("new name", f.References.NameSeen);
        Assert.Equal("new description", f.References.DescriptionSeen);
    }

    // With only the shape changing, the stored name is what gets validated.
    [Fact]
    public async Task Update_WithoutRenameValidatesAgainstTheStoredName()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(name: "existing");

        await f.Build().UpdateAsync(id, new UpdateWorkflow { Nodes = TestJson.Element("[]") });

        Assert.Equal("existing", f.References.NameSeen);
    }

    [Fact]
    public async Task Update_SurfacesWarnings()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.Vendor.Result = WorkflowValidationResult.OkWithWarnings(new[] { "unknown command" });

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateWorkflow { Nodes = TestJson.Element("[]") }));

        Assert.Equal("unknown command", Assert.Single(body.Warnings!));
    }

    // ─── delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_IsASoftDelete()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var body = Ok(await f.Build().DeleteAsync(id));

        Assert.Equal(id, body.WorkflowId);
        Assert.False((await f.Db.Set<WorkflowModel>().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Delete_UnknownWorkflow_Is404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().DeleteAsync(Guid.NewGuid())).Result);
    }

    // Production is immutable for deletes too.
    [Fact]
    public async Task Delete_ProductionWorkflow_Is409()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "production");

        Assert.Equal(409, StatusOf(await f.Build().DeleteAsync(id)));
        Assert.True((await f.Db.Set<WorkflowModel>().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Delete_AlreadyDeletedIs404()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var svc = f.Build();
        await svc.DeleteAsync(id);

        Assert.IsType<NotFoundObjectResult>((await svc.DeleteAsync(id)).Result);
    }
}
