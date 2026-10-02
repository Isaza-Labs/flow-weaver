using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Compiler;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("workflow.read")]
public class WorkflowController : ControllerBase
{
    private readonly IWorkflow _service;
    private readonly IWorkflowTrigger _triggers;
    private readonly IWorkflowExecutor _executor;
    private readonly IPromotionService _promotion;
    private readonly ICurrentUser _caller;
    private readonly Data.Db.AppDbContext _db;
    private readonly IWorkflowVersion _versions;
    private readonly IWorkflowExportService _exportService;
    private readonly Services.Workflow.IWorkflowBundleService _bundleService;
    private readonly Services.Workflow.IWorkflowBundleImporter _bundleImporter;
    private readonly ISnippetRepository _snippets;
    private readonly IEffectivePermissions _effective;
    private readonly IAppSettingsService _settings;
    private readonly ILogger<WorkflowController> _logger;

    public WorkflowController(
        IWorkflow service,
        IWorkflowTrigger triggers,
        IWorkflowExecutor executor,
        IPromotionService promotion,
        ICurrentUser caller,
        Data.Db.AppDbContext db,
        IWorkflowVersion versions,
        IWorkflowExportService exportService,
        Services.Workflow.IWorkflowBundleService bundleService,
        Services.Workflow.IWorkflowBundleImporter bundleImporter,
        ISnippetRepository snippets,
        IEffectivePermissions effective,
        IAppSettingsService settings,
        ILogger<WorkflowController> logger)
    {
        _service = service;
        _triggers = triggers;
        _executor = executor;
        _promotion = promotion;
        _caller = caller;
        _db = db;
        _versions = versions;
        _exportService = exportService;
        _bundleService = bundleService;
        _bundleImporter = bundleImporter;
        _snippets = snippets;
        _effective = effective;
        _settings = settings;
        _logger = logger;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<ListResponse<WorkflowResponse>>> Get(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? environment = null,
        [FromQuery] string? search = null)
    {
        if (string.IsNullOrEmpty(environment) && string.IsNullOrEmpty(search))
            return await _service.GetAsync(limit, offset);

        // Filter at the controller level to avoid changing IBaseService.
        (limit, offset) = Services.Common.Pagination.Clamp(limit, offset);

        var query = _db.Workflows.AsNoTracking()
            .Where(w => w.IsActive);
        if (!string.IsNullOrEmpty(environment))
            query = query.Where(w => w.Environment == environment);
        if (!string.IsNullOrEmpty(search))
            // Case-insensitive Postgres match; rides a pg_trgm index
            // on Workflow.Name when available.
            query = query.Where(w => EF.Functions.ILike(w.Name, $"%{search}%"));

        var total = await query.CountAsync();
        var workflows = await query.OrderByDescending(w => w.CreatedAt)
            .Skip(offset).Take(limit).ToListAsync();

        return new OkObjectResult(new ListResponse<WorkflowResponse>
        {
            Data = workflows.Select(w => new WorkflowResponse
            {
                WorkflowId = w.WorkflowId, Name = w.Name, Description = w.Description,
                Version = w.Version, SchemaVersion = w.SchemaVersion,
                InputSchema = w.InputSchema, Nodes = w.Nodes, Edges = w.Edges,
                Metadata = w.Metadata, CreatedBy = w.CreatedBy, CreatedAt = w.CreatedAt,
                UpdatedAt = w.UpdatedAt, Environment = w.Environment,
                PromotedFrom = w.PromotedFrom, ChangeSummary = w.ChangeSummary,
                PromotedAt = w.PromotedAt, ConversationId = w.ConversationId,
            }).ToList(),
            Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<WorkflowResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("workflow.create")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowResponse>> Post([FromBody] CreateWorkflow dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("workflow.update")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowResponse>> Update(Guid id, [FromBody] UpdateWorkflow dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("workflow.delete")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Nested read/create: triggers live under their parent workflow for
    // frontend convenience.
    [HttpGet("{workflowId:guid}/triggers")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<IActionResult> GetTriggers(
        Guid workflowId, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var triggerResult = await _triggers.GetByWorkflowAsync(workflowId, limit, offset);
        if (triggerResult.Result is not OkObjectResult ok
            || ok.Value is not ListResponse<WorkflowTriggerResponse> response)
        {
            return triggerResult.Result!;
        }

        return Ok(response.Data);
    }

    [HttpPost("{workflowId:guid}/triggers")]
    [HasPermission("trigger.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowTriggerResponse>> CreateTrigger(
        Guid workflowId, [FromBody] CreateWorkflowTrigger dto)
        => _triggers.PostForWorkflowAsync(workflowId, dto);

    // Static simulation endpoint. Reuses the AI tool handler so
    // the chat agent and the UI button share the exact same validation
    // logic. Reports structural issues (missing snippets, broken edges,
    // template-ordering violations) without touching any device.
    [HttpPost("{id:guid}/simulate")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<JsonElement>> Simulate(
        Guid id,
        [FromServices] SimulateWorkflowRunHandler handler,
        CancellationToken ct)
    {
        var args = JsonDocument.Parse($"{{\"workflow_id\":\"{id}\"}}").RootElement;
        var result = await handler.ExecuteAsync(args, ct);
        return Ok(result);
    }

    // Enqueue a workflow run. Creates a pending WorkflowRun row and a
    // `type=workflow_run` job; the worker (Sprint 2.5) picks it up and
    // calls ExecuteRunAsync. Returns 202 Accepted with the run shape so
    // the client can navigate to /runs/{id}.
    [HttpPost("{id:guid}/run")]
    [HasPermission("workflow.run")]
    [EnableRateLimiting(RateLimitingConfiguration.WorkflowRun)]
    public async Task<ActionResult<WorkflowRunResponse>> Run(
        Guid id, [FromBody] RunWorkflowRequest request, CancellationToken ct)
    {
        // Granular RBAC: the [HasPermission("workflow.run")] gate above
        // is COARSE — it only checks the caller holds workflow.run in SOME
        // context. A grant scoped by environment/resource (e.g. "run in qa only")
        // must still be honored against the CONCRETE workflow being run, or the
        // scope silently fails open (a qa-only runner could launch a draft or
        // production workflow). Mirrors PromotionService's env-conditioned
        // promote check; admin bypasses inside EffectivePermissions. System runs
        // (scheduler / git webhook) call EnqueueRunAsync directly, not this
        // endpoint, so they are unaffected.
        if (RbacModes.IsGranular((await _settings.GetAsync(ct)).RbacMode))
        {
            var wf = await _db.Workflows
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkflowId == id
                                          && w.IsActive, ct);
            // A missing workflow isn't authorized here — let EnqueueRunAsync
            // surface the not-found in its usual shape (same in both RBAC modes).
            //
            // The check is per target device: workflow.run with each device's
            // id / role / pools, plus device.exec.read|write when the graph sends
            // anything to a device. Without targets it is the environment +
            // resource check alone.
            if (wf is not null)
            {
                var denial = await new RunDeviceAuthorizer(_db, _effective).AuthorizeAsync(
                    wf.WorkflowId, wf.Environment, wf.Nodes, request.Input,
                    request.TargetDevices, request.TargetPools, ct);
                if (denial is not null)
                {
                    _logger.LogWarning(
                        "workflow.run.permission_denied workflow_id={WorkflowId} environment={Environment} capability={Capability} device_id={DeviceId} user_id={UserId}",
                        id, wf.Environment, denial.Capability, denial.DeviceId, _caller.UserId);
                    var where = denial.DeviceId is null
                        ? $"in {wf.Environment}"
                        : $"in {wf.Environment} on device '{denial.DeviceName}'";
                    return Problems.Forbidden(
                        denial.Capability == "workflow.run"
                            ? $"you do not have permission to run this workflow {where}"
                            : $"you do not have permission to run this workflow {where}: it needs {denial.Capability}",
                        code: "permission_denied");
                }
            }
        }

        try
        {
            var runId = await _executor.EnqueueRunAsync(_caller.UserId, id, request, ct);

            // Return the full run so the frontend gets the same shape as GET.
            var run = await _db.WorkflowRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkflowRunId == runId, ct);

            if (run is null)
                return Problems.Internal("run created but not found", code: "run_lookup_failed");

            return new ObjectResult(new WorkflowRunResponse
            {
                Id = run.WorkflowRunId,
                WorkflowId = run.WorkflowId,
                Status = run.Status,
                InputPayload = run.InputPayload,
                TargetDevices = run.TargetDevices,
                TargetPools = run.TargetPools,
                Trigger = run.Trigger,
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt,
                Error = run.Error,
                CreatedBy = run.CreatedBy,
                CreatedAt = run.CreatedAt,
            }) { StatusCode = 202 };
        }
        catch (WorkflowExecutorException ex)
        {
            // 409 Conflict — the workflow exists, but its prerequisites
            // (integration credentials, missing snippets, etc.) prevent
            // a run in the current state. 404 was misleading: the row
            // is reachable, the operation just can't proceed yet.
            return Problems.Conflict(ex.Message, code: "workflow_not_runnable");
        }
        catch (DagParseException ex)
        {
            return Problems.BadRequest(
                $"dag validation failed: {ex.Message}",
                code: "dag_invalid");
        }
    }

    // ─── Promotion (Sprint 3.1) ─────────────────────────────────────

    [HttpPost("{id:guid}/promote")]
    [HasPermission("workflow.promote")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowResponse>> Promote(
        Guid id, [FromBody] PromoteRequest request, CancellationToken ct)
        => _promotion.PromoteAsync(id, request, ct);

    [HttpPost("{id:guid}/rollback/{version:int}")]
    [HasPermission("workflow.rollback")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowResponse>> Rollback(
        Guid id, int version, CancellationToken ct)
        => _promotion.RollbackAsync(id, version, ct);

    [HttpGet("{id:guid}/diff")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<DiffResult>> Diff(Guid id, CancellationToken ct)
        => _promotion.DiffAsync(id, ct);

    // List workflows tagged as reusable subflows. Operators flag a
    // workflow with `metadata.is_subflow = true` (set in the editor) to
    // make it appear in the subflow node's picker. We do the filter in
    // SQL via the jsonb operator so we don't have to load every row.
    [HttpGet("subflows")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<WorkflowResponse>>> ListSubflows(
        [FromQuery] string? environment = null,
        CancellationToken ct = default)
    {
        // We fall back to reading the metadata blob and filtering in
        // memory rather than depending on a provider-specific jsonb
        // operator. Workflow counts are bounded (typically dozens)
        // so this is fine.
        var query = _db.Workflows.AsNoTracking()
            .Where(w => w.IsActive);
        if (!string.IsNullOrWhiteSpace(environment))
            query = query.Where(w => w.Environment == environment);
        var loaded = await query.OrderBy(w => w.Name).ToListAsync(ct);
        var rows = loaded.Where(IsTaggedAsSubflow).ToList();

        static bool IsTaggedAsSubflow(Models.Workflow w)
        {
            if (w.Metadata.ValueKind != JsonValueKind.Object) return false;
            return w.Metadata.TryGetProperty("is_subflow", out var el)
                && el.ValueKind == JsonValueKind.True;
        }
        return Ok(rows.Select(w => new WorkflowResponse
        {
            WorkflowId = w.WorkflowId,
            Name = w.Name,
            Description = w.Description,
            Version = w.Version,
            SchemaVersion = w.SchemaVersion,
            Environment = w.Environment,
            InputSchema = w.InputSchema,
            Nodes = w.Nodes,
            Edges = w.Edges,
            Metadata = w.Metadata,
            CreatedBy = w.CreatedBy,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt,
        }));
    }

    // Rollback risk surface. Lists nodes that block a clean
    // rollback so the editor can warn before promotion.
    [HttpGet("{id:guid}/rollback-risk")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<object>> RollbackRisk(
        Guid id,
        [FromServices] WorkflowRollbackAnalyzer analyzer,
        CancellationToken ct)
    {
        var wf = await _db.Workflows
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkflowId == id
                && w.IsActive, ct);
        if (wf is null) return Problems.NotFound("workflow", id);
        var report = await analyzer.AnalyzeAsync(wf, ct);
        return Ok(new
        {
            non_reversible = report.NonReversible.Select(r => new
            {
                snippet_id = r.SnippetId,
                snippet_name = r.SnippetName,
                snippet_type = r.SnippetType,
                kind = r.EffectiveKind.ToString(),
            }),
            requires_compensation = report.RequiresCompensation.Select(r => new
            {
                snippet_id = r.SnippetId,
                snippet_name = r.SnippetName,
                snippet_type = r.SnippetType,
                kind = r.EffectiveKind.ToString(),
            }),
            compensated = report.CompensatingFailureEdges.Select(r => new
            {
                snippet_id = r.SnippetId,
                snippet_name = r.SnippetName,
                snippet_type = r.SnippetType,
                kind = r.EffectiveKind.ToString(),
            }),
        });
    }

    [HttpPost("{id:guid}/clone")]
    [HasPermission("workflow.clone")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowResponse>> Clone(Guid id, CancellationToken ct)
        => _promotion.CloneAsync(id, ct);

    // GET versions already served by WorkflowVersionController
    // Also served via nested route for frontend convenience.
    [HttpGet("{id:guid}/versions")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<IActionResult> Versions(
        Guid id, [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        return (await _versions.GetByWorkflowAsync(id, limit, offset)).Result!;
    }

    // ─── Export (Sprint 3.2) ────────────────────────────────────────

    [HttpGet("{id:guid}/export")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<IActionResult> Export(
        Guid id,
        [FromQuery] string format = "yaml",
        CancellationToken ct = default)
    {
        // The portable format. yaml/json stay as they were for anything already
        // consuming them, but only this one survives crossing instances.
        if (string.Equals(format, "bundle", StringComparison.OrdinalIgnoreCase))
        {
            var bundle = await _bundleService.BuildAsync(id, ct);
            var json = System.Text.Json.JsonSerializer.Serialize(
                bundle, WorkflowBundleReader.SerializerOptions);
            Response.Headers.ContentDisposition =
                $"attachment; filename=\"{WorkflowBundleReader.FileName(bundle.Workflow.Name)}\"";
            return Content(json, "application/json");
        }

        var payload = await _exportService.ExportAsync(id, format, ct);
        Response.Headers.ContentDisposition = $"attachment; filename=\"{payload.Filename}\"";
        return Content(payload.Content, payload.ContentType);
    }

    // ─── Import ─────────────────────────────────────────────────────
    //
    // Counterpart of Export. Accepts a YAML or JSON document produced
    // by the export endpoint and creates a new draft workflow from it.
    // The document's `workflow.id` is intentionally ignored — imports
    // always land as a fresh draft. Schema + reference validation runs
    // through the standard create path so the import surface is no
    // weaker than POST /workflow.
    [HttpPost("import")]
    [HasPermission("workflow.import")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<WorkflowResponse>> Import(
        [FromQuery] string format = "yaml",
        CancellationToken ct = default)
    {
        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync(ct);

        // A bundle carries portable identities for everything it references,
        // so it resolves deterministically against this instance. Anything else
        // is the legacy shape (GUIDs only) and keeps the old behaviour.
        if (WorkflowBundleReader.LooksLikeBundle(raw, format))
            return await ImportBundleAsync(raw, ct);

        var payload = _exportService.ParseImport(raw, format);

        var dto = new CreateWorkflow
        {
            Name = payload.Name!,
            Description = payload.Description,
            Nodes = payload.Nodes,
            Edges = payload.Edges,
            InputSchema = payload.InputSchema,
            Metadata = payload.Metadata,
        };

        return await _service.PostAsync(dto);
    }

    // Deterministic path for a bundle exported by another FlowWeaver (or by
    // another engine that uses the same wire format): resolve every dependency by identity,
    // create the snippets and sub-workflows the bundle carries, rewrite the
    // node references, create the workflow through the SAME create path as
    // everything else (schema + reference validation still applies), then
    // create its triggers disabled. All of it in one transaction.
    //
    // Resolution throws ValidationException listing what is missing rather than
    // substituting anything — the whole point of the format is that a shared
    // workflow either arrives intact or says exactly why it cannot.
    private async Task<ActionResult<WorkflowResponse>> ImportBundleAsync(
        string raw, CancellationToken ct)
    {
        var bundle = WorkflowBundleReader.Parse(raw);
        var response = await _bundleImporter.ImportAsync(bundle, ct);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Workflow",
            routeValues: new { id = response.WorkflowId },
            value: response);
    }
}
