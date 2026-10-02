using System.Collections.Concurrent;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Validation;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/workflow/import")]
[HasPermission("workflow.import")]
public class WorkflowImportController : ControllerBase
{
    private const int MaxUploadBytes = 5 * 1024 * 1024; // 5 MiB

    // Best-effort in-process gate around "lookup-or-create catch-all
    // integration". This narrows — but does NOT fully close — the race
    // where two concurrent imports both insert a duplicate
    // `imported_actions` row: the gate serializes the check+insert
    // within the process, but READ COMMITTED isolation means a sibling
    // import in another transaction still can't see the uncommitted
    // row until the first transaction commits. The full fix is a
    // UNIQUE(Name) index (or pg_advisory_xact_lock) — left
    // as follow-up because it needs a migration. Until then the gate
    // covers the most common case (back-to-back imports finishing
    // before the next one starts).
    private static readonly SemaphoreSlim CatchAllLock = new(1, 1);

    private readonly ImportDraftCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly AppDbContext _db;
    private readonly IWorkflowSchemaValidator _schemaValidator;
    private readonly IWorkflowReferenceValidator _referenceValidator;
    private readonly SnippetStubBuilder _stubBuilder;
    private readonly IResourcePermissionService _permissions;
    private readonly flow_weaver_backend.Services.Settings.IAppSettingsService _appSettings;
    private readonly Services.Workflow.IWorkflowBundleImporter _bundleImporter;
    private readonly IPolicyEvaluator _policies;
    private readonly ILogger<WorkflowImportController> _logger;

    public WorkflowImportController(
        ImportDraftCache cache,
        IServiceScopeFactory scopeFactory,
        ICurrentUser caller,
        IAuditLogger audit,
        AppDbContext db,
        IWorkflowSchemaValidator schemaValidator,
        IWorkflowReferenceValidator referenceValidator,
        SnippetStubBuilder stubBuilder,
        IResourcePermissionService permissions,
        flow_weaver_backend.Services.Settings.IAppSettingsService appSettings,
        Services.Workflow.IWorkflowBundleImporter bundleImporter,
        IPolicyEvaluator policies,
        ILogger<WorkflowImportController> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _caller = caller;
        _audit = audit;
        _db = db;
        _schemaValidator = schemaValidator;
        _referenceValidator = referenceValidator;
        _stubBuilder = stubBuilder;
        _permissions = permissions;
        _appSettings = appSettings;
        _bundleImporter = bundleImporter;
        _policies = policies;
        _logger = logger;
    }

    // Mirrors WorkflowService.AuthorizeAsync: opt-in. When granular gating
    // is not enabled, every caller that already satisfies the global
    // Operator policy is allowed through.
    private async Task<bool> AuthorizeAsync(Guid workflowId, string requiredRole, CancellationToken ct)
    {
        var settings = await _appSettings.GetAsync(ct);
        if (!settings.PermissionsGranularGatingEnabled) return true;
        return await _permissions.HasAtLeastAsync(
            ResourceTypes.Workflow, workflowId, requiredRole, ct);
    }

    // POST /api/workflow/import/analyze
    // Accepts the raw file in the request body. Detection + translation
    // + dependency resolution run in the background via Task.Run; the
    // response returns immediately with the import token.
    [HttpPost("analyze")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Analyze(
        [FromQuery] string? format_hint = null,
        CancellationToken ct = default)
    {
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var raw = ms.ToArray();
        if (raw.Length == 0)
            return Problems.BadRequest("empty upload", code: "empty_upload");
        if (raw.Length > MaxUploadBytes)
            return Problems.PayloadTooLarge(
                $"upload exceeds {MaxUploadBytes / 1024} KiB",
                code: "upload_too_large");

        var draft = _cache.Create(_caller.UserId, format_hint ?? "", raw);
        _logger.LogInformation(
            "import.analyze.started user_id={UserId} token={Token} bytes={Bytes}",
            _caller.UserId, draft.Token, raw.Length);

        await _audit.LogAsync("workflow_import", null, "workflow_import.started",
            after: new { token = draft.Token, bytes = raw.Length, format_hint });

        // Fire-and-forget. We deliberately don't await so the user gets
        // the token immediately and can connect to the SSE stream.
        // The draft owns a CTS that DELETE / cache sweeps cancel — that
        // token tears down the LLM calls and DB writes mid-pipeline so
        // we don't keep burning provider tokens after the user bailed.
        var pipelineToken = draft.CancellationToken;
        _ = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<WorkflowImportPipeline>();
            var userOverride = scope.ServiceProvider.GetRequiredService<MutableCurrentUser>();
            userOverride.Bind(draft.UserId, username: null);
            await pipeline.RunAsync(draft, pipelineToken);
        }, CancellationToken.None);

        return Accepted(new { import_token = draft.Token, status = "pending" });
    }

    // GET /api/workflow/import/{token}
    // Synchronous poll. Returns the latest known state of the draft.
    [HttpGet("{token:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public IActionResult Get(Guid token)
    {
        var draft = _cache.Get(token, _caller.UserId);
        if (draft is null) return Problems.NotFound("import draft", token);
        return Ok(new
        {
            import_token = draft.Token,
            status = draft.Status.ToString().ToLowerInvariant(),
            progress = draft.ProgressMessage,
            report = draft.Report,
            error = draft.Error,
        });
    }

    // GET /api/workflow/import/{token}/stream
    // Server-Sent Events. Emits one frame per status change + one per
    // progress note. The stream closes when the draft reaches Ready,
    // Failed, or Committed.
    [HttpGet("{token:guid}/stream")]
    public async Task Stream(Guid token, CancellationToken ct)
    {
        var draft = _cache.Get(token, _caller.UserId);
        if (draft is null)
        {
            Response.StatusCode = 404;
            await Response.WriteAsync("import draft not found", ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Push the current state immediately so a late subscriber catches up.
        await WriteSseAsync(Response, "snapshot", new
        {
            status = draft.Status.ToString().ToLowerInvariant(),
            progress = draft.ProgressMessage,
            report = draft.Report,
            error = draft.Error,
        }, ct);

        await foreach (var evt in draft.Events.ReadAllAsync(ct))
        {
            await WriteSseAsync(Response, evt.Type, new
            {
                status = evt.Status.ToString().ToLowerInvariant(),
                progress = evt.Message,
                report = draft.Report,
                error = evt.Error,
            }, ct);
            if (evt.Status is ImportDraftStatus.Ready or ImportDraftStatus.Failed or ImportDraftStatus.Committed)
                break;
        }
    }

    // POST /api/workflow/import/{token}/commit
    // Applies the user's resolutions (rename/replace/duplicate handling,
    // stubbing missing snippets, creating needs_config integrations) and
    // persists the workflow.
    [HttpPost("{token:guid}/commit")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Commit(
        Guid token,
        [FromBody] CommitImportRequest dto,
        CancellationToken ct)
    {
        var draft = _cache.Get(token, _caller.UserId);
        if (draft is null) return Problems.NotFound("import draft", token);
        if (draft.Status != ImportDraftStatus.Ready)
            return Problems.Conflict($"draft not ready (status={draft.Status})", code: "draft_not_ready");

        var report = draft.Report!;
        var conflictRes = dto.ConflictResolution?.ToLowerInvariant() ?? "fresh_copy";


        // A bundle resolves by identity and carries everything it references, so
        // there is nothing for the wizard's resolutions to resolve. It goes to the
        // same importer POST /api/Workflow/import uses — one transaction, fresh
        // local ids, references rewritten, snippets reused where they already
        // exist. Two implementations of one import would drift, and this is the
        // one with the tests.
        if (report.Bundle is not null && conflictRes != "keep_existing")
        {
            var bundle = Services.Workflow.WorkflowBundleReader.Parse(
                System.Text.Encoding.UTF8.GetString(draft.RawBody));
            var imported = await _bundleImporter.ImportAsync(bundle, ct);
            draft.MarkCommitted();
            _logger.LogInformation(
                "import.bundle.committed token={Token} workflow_id={WorkflowId}",
                draft.Token, imported.WorkflowId);
            await _audit.LogAsync("workflow", imported.WorkflowId, "workflow_import.bundle_committed",
                after: new { token = draft.Token, kind = report.Bundle.Kind });

            // The wizard's shape, not the workflow's. It reads `workflow_id`,
            // `name` and `environment`, and then ITERATES `warnings` — and a
            // WorkflowResponse carries `warnings: null`, so returning one made the
            // client throw on `for (const w of res.warnings)` after a successful
            // import: the workflow was created, it ran, and the screen said
            // "Commit failed".
            //
            // The resolution's notes are the warnings worth surfacing here. They
            // name what the operator still has to configure — a credential this
            // instance must hold, a trigger left disabled — so the wrong shape was
            // hiding them as well as breaking the screen.
            return Ok(new
            {
                workflow_id = imported.WorkflowId,
                name = imported.Name,
                environment = imported.Environment,
                warnings = (imported.ImportNotes ?? Array.Empty<string>())
                    .Concat(imported.Warnings ?? Array.Empty<string>())
                    .ToArray(),
            });
        }

        // An import is a create (or an in-place update), never a promotion. This
        // path used to write `target_environment` straight onto the new row, which
        // let any caller with workflow.import land a workflow in qa or production
        // without the QA gate, the second approver or a policy check. Imports now
        // always land in draft; reaching qa/production goes through promote.
        //
        // Checked after the bundle branch on purpose: the bundle importer never
        // honoured the field (it always creates a draft), so a client that still
        // sends one there keeps getting the same answer it always got.
        if (!string.IsNullOrWhiteSpace(dto.TargetEnvironment)
            && !string.Equals(dto.TargetEnvironment, "draft", StringComparison.OrdinalIgnoreCase))
        {
            return Problems.BadRequest(
                "imports always land in draft — promote the workflow afterwards",
                code: "import_target_environment_not_allowed");
        }

        // keep_existing cancels the import outright. We mark the draft
        // committed so the user's choice is auditable; no new workflow
        // lands. Returns 200 so the wizard can show "nothing to do"
        // instead of an error.
        if (conflictRes == "keep_existing")
        {
            draft.MarkCommitted();
            await _audit.LogAsync("workflow_import", null, "workflow_import.skipped_keep_existing",
                after: new { token = draft.Token });
            return Ok(new
            {
                workflow_id = report.Conflicts.NameCollision?.MatchingWorkflowId,
                name = (string?)null,
                environment = report.Conflicts.NameCollision?.MatchingWorkflowEnvironment,
                warnings = new[] { "Import cancelled — kept existing workflow." },
            });
        }

        // FU-1: wrap the whole commit in a transaction. The pipeline
        // persists snippets/integrations BEFORE the workflow (so the
        // reference validator can resolve their ids) — without a tx,
        // a failure in the workflow save leaves orphan stubs/integrations.
        // EF InMemory (used by tests) doesn't support transactions; we
        // skip the wrap there since the test never crashes mid-save.
        var providerSupportsTx = _db.Database.ProviderName
            != "Microsoft.EntityFrameworkCore.InMemory";
        await using var tx = providerSupportsTx
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // Per-resource gating only blocks REPLACE on an existing
        // workflow. Importing as a new row is a create, which the global
        // Operator policy already authorises. Replace soft-deletes the
        // target, so it takes the same owner grant DELETE /api/Workflow does.
        if (conflictRes == "replace" && report.Conflicts.NameCollision is { } collision)
        {
            if (!await AuthorizeAsync(collision.MatchingWorkflowId, ResourceRoles.Owner, ct))
            {
                return Problems.Forbidden("missing_owner_grant for replace target", code: "missing_owner_grant");
            }
            // Soft-delete the existing row so the import lands without
            // a duplicate. Audit captures who replaced what.
            var existing = await _db.Workflows
                .FirstOrDefaultAsync(w => w.WorkflowId == collision.MatchingWorkflowId, ct);
            // Production rows are immutable everywhere else (update and delete
            // both answer production_immutable); an import must not be the way
            // around that.
            if (existing is not null && existing.Environment == "production")
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                return Problems.Conflict(
                    "a production workflow cannot be replaced by an import — clone it to draft instead",
                    code: "production_immutable");
            }
            if (existing is not null)
            {
                existing.IsActive = false;
                existing.UpdatedAt = DateTime.UtcNow;
                await _audit.LogAsync("workflow", existing.WorkflowId, "workflow.replaced_by_import",
                    before: new { existing.Name, existing.Version, existing.Environment },
                    after: new { token = draft.Token });
            }
        }

        // Apply user-chosen actions on missing snippets.
        var snippetIdRemap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        // User opted to skip these snippets entirely. Every node in the
        // proposed workflow whose `snippet_id` matches one of these
        // strings will be dropped before the workflow is persisted,
        // together with the edges that incident on those nodes.
        var skippedSnippetIds = new HashSet<string>(StringComparer.Ordinal);

        // Missing snippets whose InferredType is `integration_action` get
        // a special treatment: they DO NOT become a Snippet row. Instead
        // we materialise a stub Integration + IntegrationAction and bind
        // the node to them via the virtual `integration_action` sentinel.
        // Without this branch the wizard would create a `Type =
        // "integration_action"` Snippet that fails the post-import
        // reference validator (which requires every such node to carry
        // `config_overrides.integration_id` + `action_id` GUIDs).
        // Key = import-side id; value = (integration_id, action_id,
        // integration_name, action_name).
        var snippetToIntegrationAction =
            new Dictionary<string, (Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)>(
                StringComparer.Ordinal);

        // Lazily-materialised catch-all integration for auto-stubbed
        // integration_action references. We only hit the DB if at least
        // one missing snippet ends up needing this path.
        Guid? catchAllIntegrationId = null;
        string? catchAllIntegrationName = null;
        async Task<(Guid Id, string Name)> EnsureCatchAllAsync()
        {
            if (catchAllIntegrationId is not null && catchAllIntegrationName is not null)
                return (catchAllIntegrationId.Value, catchAllIntegrationName);

            // Best-effort within-process serialisation for the common
            // back-to-back-imports case. See the comment on `CatchAllLock`
            // for the residual cross-transaction race that this does not
            // solve.
            await CatchAllLock.WaitAsync(ct);
            try
            {
                // Re-check after acquiring — another import may have
                // already inserted the catch-all while we waited.
                var existing = await _db.Integrations
                    .AsNoTracking()
                    .Where(i => i.IsActive
                                && i.Name == "imported_actions")
                    .Select(i => new { i.IntegrationId, i.Name })
                    .FirstOrDefaultAsync(ct);
                if (existing is not null)
                {
                    catchAllIntegrationId = existing.IntegrationId;
                    catchAllIntegrationName = existing.Name;
                    return (existing.IntegrationId, existing.Name);
                }

                var fresh = new IntegrationModel
                {
                    IntegrationId = Guid.NewGuid(),
                    Name = "imported_actions",
                    Type = "generic_rest",
                    BaseURL = "",
                    AuthConfig = JsonDocument.Parse("{}").RootElement.Clone(),
                    Headers = JsonDocument.Parse("{}").RootElement.Clone(),
                    HealthCheck = JsonDocument.Parse("{}").RootElement.Clone(),
                    Status = IntegrationStatus.NeedsConfig,
                    Enabled = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                _db.Integrations.Add(fresh);
                // No SaveChanges here: under READ COMMITTED the row
                // wouldn't be visible to sibling transactions anyway,
                // and the outer commit pipeline already flushes pending
                // integrations before running the reference validator.
                await _audit.LogAsync("integration", fresh.IntegrationId, "integration.created_needs_config",
                    after: new
                    {
                        integration_name = fresh.Name,
                        reason = "auto-created catch-all for integration_action stubs during import",
                        triggered_by_import = draft.Token,
                    });
                catchAllIntegrationId = fresh.IntegrationId;
                catchAllIntegrationName = fresh.Name;
                return (fresh.IntegrationId, fresh.Name);
            }
            finally
            {
                CatchAllLock.Release();
            }
        }

        foreach (var ms_ in report.MissingDependencies.Snippets)
        {
            if (!dto.Snippets.TryGetValue(ms_.IdInImport, out var action))
            {
                return Problems.BadRequest(
                    $"no resolution provided for missing snippet '{ms_.IdInImport}'",
                    code: "snippet_resolution_missing");
            }
            switch (action.Action)
            {
                case "stub":
                    if (string.Equals(ms_.InferredType, "integration_action", StringComparison.OrdinalIgnoreCase))
                    {
                        // Materialise a (catch-all integration, action) pair
                        // instead of a Snippet. The node will be rewritten
                        // to the virtual sentinel below.
                        var (integrationId, integrationName) = await EnsureCatchAllAsync();
                        var actionName = ms_.IdInImport;
                        var actionRow = new flow_weaver_backend.Models.IntegrationAction
                        {
                            IntegrationActionId = Guid.NewGuid(),
                            IntegrationId = integrationId,
                            Name = actionName,
                            Method = InferHttpMethodFromActionName(actionName),
                            Path = "",
                            Description = $"Auto-created from import for node referencing '{actionName}'. Set the path + headers + body schema in /integrations/{integrationId} before running.",
                            Category = "imported",
                            IsActive = true,
                            // The palette query at /api/integrationaction/actions/all
                            // filters by `Enabled` AND `IsActive`. Without
                            // Enabled=true the node's action picker stays empty
                            // ("Integration has no actions defined yet").
                            Enabled = true,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        };
                        _db.IntegrationActions.Add(actionRow);
                        snippetToIntegrationAction[ms_.IdInImport] =
                            (integrationId, actionRow.IntegrationActionId, integrationName, actionName);
                        break;
                    }
                    var stub = _stubBuilder.Build(ms_.IdInImport, ms_.InferredType, _caller.Username);
                    _db.Snippets.Add(stub);
                    snippetIdRemap[ms_.IdInImport] = stub.SnippetId;
                    break;
                case "generated":
                    if (action.GeneratedSnippet is null)
                        return Problems.BadRequest(
                            $"action=generated for '{ms_.IdInImport}' requires generated_snippet payload",
                            code: "generated_snippet_missing");
                    var generatedType = TryGetString(action.GeneratedSnippet.Value, "type");
                    if (string.Equals(generatedType, "integration_action", StringComparison.OrdinalIgnoreCase))
                    {
                        // The AI sometimes drafts integration_action snippets
                        // with a structured `code` field carrying
                        // (integration_name, action_name, params, ...) plus
                        // a literal "REPLACE_WITH_INTEGRATION_ID" placeholder
                        // — none of that is a real Snippet. Materialise an
                        // Integration + IntegrationAction from it and rewrite
                        // the node to the virtual sentinel, same path the
                        // stub branch above takes.
                        var (integrationId, actionId, integrationName, actionName) =
                            await MaterialiseIntegrationActionFromGeneratedAsync(
                                ms_.IdInImport, action.GeneratedSnippet.Value, EnsureCatchAllAsync);
                        snippetToIntegrationAction[ms_.IdInImport] =
                            (integrationId, actionId, integrationName, actionName);
                        break;
                    }
                    var gen = HydrateGeneratedSnippet(action.GeneratedSnippet.Value);
                    _db.Snippets.Add(gen);
                    snippetIdRemap[ms_.IdInImport] = gen.SnippetId;
                    break;
                case "map":
                    if (action.TargetId is null)
                        return Problems.BadRequest(
                            $"action=map for '{ms_.IdInImport}' requires target_id",
                            code: "snippet_map_target_required");
                    if (string.Equals(ms_.InferredType, "integration_action", StringComparison.OrdinalIgnoreCase))
                    {
                        // The target is an IntegrationId, not a SnippetId
                        // — the wizard surfaced existing integrations as
                        // mapping candidates for integration_action stubs.
                        // Materialise (or reuse) an IntegrationAction under
                        // the chosen integration so the node dispatches via
                        // the virtual sentinel like the stub / generated
                        // paths above.
                        var (integrationId, actionId, integrationName, actionName) =
                            await MapIntegrationActionToExistingIntegrationAsync(
                                ms_.IdInImport, action.TargetId.Value, ct);
                        if (integrationId == Guid.Empty)
                            return Problems.BadRequest(
                                $"target integration '{action.TargetId.Value}' not found for '{ms_.IdInImport}'",
                                code: "snippet_target_integration_missing");
                        snippetToIntegrationAction[ms_.IdInImport] =
                            (integrationId, actionId, integrationName, actionName);
                        break;
                    }
                    snippetIdRemap[ms_.IdInImport] = action.TargetId.Value;
                    break;
                case "skip":
                    // User opted out of creating this snippet. Record
                    // the id so we can drop matching nodes (and their
                    // incident edges) from the imported workflow
                    // further down. No DB row is created.
                    skippedSnippetIds.Add(ms_.IdInImport);
                    break;
                default:
                    return Problems.BadRequest(
                        $"unknown snippet action '{action.Action}'",
                        code: "snippet_action_unknown");
            }
        }

        // Apply user-chosen actions on missing integrations.
        var integrationIdRemap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        // Track integrations created via `create_needs_config` so we
        // can auto-fabricate IntegrationAction rows for every action
        // the imported workflow references under them. Without this,
        // a fresh import ends up with an integration in needs_config
        // status BUT zero actions — every node still fails to resolve
        // `action_id` and the user has to create each action by hand
        // before the import is usable.
        var newlyCreatedIntegrationIds = new HashSet<Guid>();
        foreach (var mi in report.MissingDependencies.Integrations)
        {
            if (!dto.Integrations.TryGetValue(mi.IdInImport, out var action))
            {
                return Problems.BadRequest(
                    $"no resolution provided for missing integration '{mi.IdInImport}'",
                    code: "integration_resolution_missing");
            }
            switch (action.Action)
            {
                case "create_needs_config":
                    var integ = new IntegrationModel
                    {
                        IntegrationId = Guid.NewGuid(),
                        Name = mi.IdInImport,
                        Type = mi.InferredType ?? "generic_rest",
                        BaseURL = mi.InferredBaseUrl ?? "",
                        AuthConfig = JsonDocument.Parse("{}").RootElement.Clone(),
                        Headers = JsonDocument.Parse("{}").RootElement.Clone(),
                        HealthCheck = JsonDocument.Parse("{}").RootElement.Clone(),
                        Status = IntegrationStatus.NeedsConfig,
                        Enabled = true,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    _db.Integrations.Add(integ);
                    integrationIdRemap[mi.IdInImport] = integ.IntegrationId;
                    newlyCreatedIntegrationIds.Add(integ.IntegrationId);
                    await _audit.LogAsync("integration", integ.IntegrationId, "integration.created_needs_config",
                        after: new
                        {
                            integration_name = integ.Name,
                            base_url = integ.BaseURL,
                            triggered_by_import = draft.Token,
                        });
                    break;
                case "map":
                    if (action.TargetId is null)
                        return Problems.BadRequest(
                            $"action=map for '{mi.IdInImport}' requires target_id",
                            code: "integration_map_target_required");
                    integrationIdRemap[mi.IdInImport] = action.TargetId.Value;
                    break;
                default:
                    return Problems.BadRequest(
                        $"unknown integration action '{action.Action}'",
                        code: "integration_action_unknown");
            }
        }

        // Drop nodes whose snippet_id was marked "skip" by the user,
        // along with edges incident on those nodes. Done BEFORE the
        // rewrite so the reference validator never sees the stale ids.
        var skipFilterWarnings = new List<string>();
        var proposedWorkflow = skippedSnippetIds.Count > 0
            ? DropSkippedNodes(report.ProposedWorkflow, skippedSnippetIds, skipFilterWarnings)
            : report.ProposedWorkflow;

        // Now rewrite the proposed workflow with the resolved ids.
        var rewritten = RewriteReferences(
            proposedWorkflow, snippetIdRemap, integrationIdRemap, snippetToIntegrationAction);
        var name = ResolveName(rewritten, conflictRes, dto.NewName, report.Conflicts.NameCollision);

        var nodes = NormalizeNodesForV1Schema(rewritten.GetProperty("nodes"));
        var edges = NormalizeEdgesForV1Schema(rewritten.GetProperty("edges"));

        // For every freshly-created `needs_config` integration, fabricate
        // an IntegrationAction row per (integration_id, action_name)
        // pair the workflow references. Method is inferred from the
        // action name's verb (`get*` → GET, `create*` → POST, …);
        // path stays empty — the user fills it under `/integrations/<id>`
        // before activating the integration. Without this step the
        // resolver below would warn for every action because the new
        // integration starts with zero actions in the catalogue.
        var autoCreationWarnings = await AutoCreateIntegrationActionsAsync(
            nodes, newlyCreatedIntegrationIds, ct);

        // Foreign translators (Itential, n8n, AgentTranslator) set
        // `integration_id` + `action_name` on integration_action nodes
        // but can't reach the DB to resolve `action_id` / `method` /
        // `path`. Without those, the editor shows the node as
        // "Integration: Unknown / Endpoint: GET" and the runtime can't
        // dispatch. Resolve here by joining (IntegrationId, Name) —
        // unique matches get fully hydrated; ambiguous names are left
        // for the user to pick in the editor's action picker.
        (nodes, var actionResolutionWarnings) = await ResolveIntegrationActionsAsync(nodes, ct);

        var schemaResult = _schemaValidator.Validate(nodes, edges);
        if (!schemaResult.IsValid)
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            return BadRequest(new
            {
                error = "imported workflow fails v1 schema validation after rewriting",
                details = schemaResult.Errors,
            });
        }

        // Persist the staged snippets / integrations BEFORE the
        // reference validator runs — it queries the DB so the new rows
        // need to be saved (otherwise map=existing-id would pass but
        // stub=new-id would fail).
        await _db.SaveChangesAsync(ct);

        // S15: re-check references after the user's resolutions
        // (mapping, stubbing, generating). Catches typos in
        // `target_id` and other cases where the remap produced an id
        // that doesn't resolve to a real row.
        // Updating a structural duplicate in place edits an existing graph, so its
        // stored nodes are the baseline for the permission gates. Every other
        // resolution creates a new workflow: everything in it is new.
        JsonElement? previousNodes = null;
        if (dto.DuplicateAction == "update_existing"
            && report.Conflicts.StructuralDuplicate is { } duplicate)
        {
            previousNodes = await _db.Workflows
                .AsNoTracking()
                .Where(w => w.WorkflowId == duplicate.MatchingWorkflowId && w.IsActive)
                .Select(w => (JsonElement?)w.Nodes)
                .FirstOrDefaultAsync(ct);
        }

        var refResult = await _referenceValidator.ValidateWithContextAsync(nodes,
            ReadWrappedString(rewritten, "name"),
            ReadWrappedString(rewritten, "description"),
            ct,
            previousNodes);
        if (!refResult.IsValid)
        {
            // FU-1: rollback the staged snippets / integrations so the
            // user can retry with corrected resolutions without leaving
            // orphan rows in /snippets and /integrations.
            if (tx is not null) await tx.RollbackAsync(ct);
            return BadRequest(new
            {
                error = "imported workflow references resources that do not exist or are not active",
                details = refResult.Errors,
                warnings = refResult.Warnings,
            });
        }

        // FU-5: `update_existing` keeps the structural duplicate's
        // WorkflowId and history intact instead of soft-deleting +
        // inserting (which is what `replace` does). The current state
        // is captured as a WorkflowVersion snapshot so rollback still
        // has somewhere to revert to, then the row is mutated in
        // place. Cosmetic fields (CreatedBy, CreatedAt) are preserved;
        // structural fields (Nodes, Edges, Version+1) are overwritten;
        // Metadata is merged so flags like is_subflow survive.
        WorkflowModel workflow;
        var isUpdateInPlace = dto.DuplicateAction == "update_existing"
            && report.Conflicts.StructuralDuplicate is { } sd;
        if (isUpdateInPlace)
        {
            var dupId = report.Conflicts.StructuralDuplicate!.MatchingWorkflowId;
            // Same gate as `replace` — needs per-resource editor.
            if (!await AuthorizeAsync(dupId, ResourceRoles.Editor, ct))
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                return Problems.Forbidden(
                    "missing_editor_grant for update_existing target",
                    code: "missing_editor_grant");
            }

            workflow = await _db.Workflows
                .FirstOrDefaultAsync(w => w.WorkflowId == dupId
                    && w.IsActive, ct)
                ?? throw new InvalidOperationException(
                    $"structural duplicate {dupId} disappeared between analyze and commit");

            if (workflow.Environment == "production")
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                return Problems.Conflict(
                    "a production workflow cannot be updated by an import — clone it to draft instead",
                    code: "production_immutable");
            }

            // Same policy check PUT /api/Workflow applies to an update.
            var updateBlocked = await EvaluateImportPolicyAsync(
                "update", workflow.Environment, name,
                ReadWrappedString(rewritten, "description"), nodes, ct);
            if (updateBlocked is not null)
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                return updateBlocked;
            }

            // Snapshot the pre-update state so rollback has somewhere to go.
            _db.WorkflowVersions.Add(new flow_weaver_backend.Models.WorkflowVersion
            {
                WorkflowVersionId = Guid.NewGuid(),
                WorkflowId = workflow.WorkflowId,
                Version = workflow.Version,
                Nodes = workflow.Nodes,
                Edges = workflow.Edges,
                Services = JsonDocument.Parse("{}").RootElement,
                PromotedBy = _caller.Username ?? "import",
                PromotedAt = DateTime.UtcNow,
                ChangeSummary = $"Captured before update_existing import (token={draft.Token})",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // Apply the import in place. WorkflowId stays the same so
            // grants, audit history, and outstanding runs keep pointing
            // at the right row.
            workflow.Name = name;
            workflow.Description = ReadWrappedString(rewritten, "description");
            workflow.Version += 1;
            workflow.InputSchema = TryGetElement(rewritten, "input_schema");
            workflow.Nodes = nodes;
            workflow.Edges = edges;
            workflow.Metadata = MergeMetadata(workflow.Metadata, TryGetElement(rewritten, "metadata"));
            workflow.ChangeSummary = $"Updated by import (token={draft.Token})";
            workflow.UpdatedAt = DateTime.UtcNow;
            // Environment and CreatedBy / CreatedAt are intentionally preserved.

            await _audit.LogAsync("workflow", workflow.WorkflowId, "workflow_import.updated_existing",
                before: new { previous_version = workflow.Version - 1 },
                after: new { new_version = workflow.Version, token = draft.Token });
        }
        else
        {
            // Same policy check POST /api/Workflow applies to a create.
            var createBlocked = await EvaluateImportPolicyAsync(
                "create", "draft", name,
                ReadWrappedString(rewritten, "description"), nodes, ct);
            if (createBlocked is not null)
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                return createBlocked;
            }

            workflow = new WorkflowModel
            {
                WorkflowId = Guid.NewGuid(),
                Name = name,
                Description = ReadWrappedString(rewritten, "description"),
                Version = 1,
                SchemaVersion = "v1",
                InputSchema = TryGetElement(rewritten, "input_schema"),
                Nodes = nodes,
                Edges = edges,
                Metadata = TryGetElement(rewritten, "metadata"),
                Environment = "draft",
                CreatedBy = _caller.Username,
                ChangeSummary = $"Imported via wizard (token={draft.Token})",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.Workflows.Add(workflow);
        }
        try
        {
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            throw;
        }

        await _audit.LogAsync("workflow", workflow.WorkflowId, "workflow_import.committed",
            after: new
            {
                token = draft.Token,
                conflict_resolution = conflictRes,
                stubbed_snippets = snippetIdRemap.Count,
                created_integrations = integrationIdRemap.Count(kv =>
                    report.MissingDependencies.Integrations.Any(mi =>
                        mi.IdInImport == kv.Key)),
                format_detected = report.FormatDetected,
            });
        draft.MarkCommitted();

        // Every integration we created above (via create_needs_config or
        // the catch-all) starts in NeedsConfig. The `map` branch
        // re-uses existing integrations whose status we didn't touch.
        // Counting from `newlyCreatedIntegrationIds` is both cheaper
        // (no ChangeTracker walk) and unambiguous about what we're
        // reporting back to the user.
        var integrationsNeedingConfigCount = newlyCreatedIntegrationIds.Count;

        var combinedWarnings = new List<string>();
        if (integrationsNeedingConfigCount > 0)
            combinedWarnings.Add($"{integrationsNeedingConfigCount} integration(s) created in needs_config status. Configure credentials before running.");
        combinedWarnings.AddRange(skipFilterWarnings);
        combinedWarnings.AddRange(autoCreationWarnings);
        combinedWarnings.AddRange(actionResolutionWarnings);

        return Ok(new
        {
            workflow_id = workflow.WorkflowId,
            name = workflow.Name,
            environment = workflow.Environment,
            warnings = combinedWarnings.ToArray(),
        });
    }

    // Mirrors WorkflowService's create/update policy gate so an import can't
    // land a graph a policy would have refused through the normal endpoints.
    // Returns the 403 to send, or null when the operation is allowed.
    private async Task<IActionResult?> EvaluateImportPolicyAsync(
        string action, string environment, string name, string? description,
        JsonElement nodes, CancellationToken ct)
    {
        var decision = await _policies.EvaluateAsync(new PolicyEvaluationContext(
            Action: action,
            Environment: environment,
            WorkflowName: name,
            WorkflowDescription: description,
            Nodes: nodes,
            DeviceRoles: Array.Empty<string>(),
            DevicePoolNames: Array.Empty<string>()), ct);
        if (decision.Allowed) return null;

        _logger.LogWarning(
            "import.commit.policy_blocked action={Action} policy={Policy} reason={Reason}",
            action, decision.PolicyName, decision.Reason);
        await _audit.LogAsync("workflow_import", null, "workflow_import.policy_blocked",
            after: new { action, policy = decision.PolicyName, reason = decision.Reason });
        return new ObjectResult(new
        {
            error = "policy_blocked",
            policy = decision.PolicyName,
            reason = decision.Reason,
        })
        { StatusCode = 403 };
    }

    // DELETE /api/workflow/import/{token}
    [HttpDelete("{token:guid}")]
    public IActionResult Delete(Guid token)
    {
        // Idempotent 204 either way: the wizard's cleanup must not fail when the
        // draft is already gone, and answering the same for someone else's token
        // keeps it from being probed. Only the uploader's draft is removed — an
        // admin keeps the kill switch for a runaway analyze pipeline, since
        // cancelling one stops its LLM spend.
        if (_caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase)))
            _cache.Delete(token);
        else
            _cache.Delete(token, _caller.UserId);
        return NoContent();
    }

    // POST /api/workflow/import/{token}/generate-snippet
    // Asks the agent to draft a Snippet body for one of the missing
    // references. Returns the proposal — the user inspects it before
    // including it in the commit body under `action: "generated"`.
    [HttpPost("{token:guid}/generate-snippet")]
    [EnableRateLimiting(RateLimitingConfiguration.AiChat)]
    public async Task<IActionResult> GenerateSnippet(
        Guid token,
        [FromBody] GenerateSnippetRequest dto,
        [FromServices] flow_weaver_backend.Services.Ai.Tools.Handlers.GenerateSnippetForImportHandler handler,
        CancellationToken ct)
    {
        var draft = _cache.Get(token, _caller.UserId);
        if (draft is null || draft.Report is null)
            return Problems.NotFound("import draft (not yet analyzed)", token);

        var missing = draft.Report.MissingDependencies.Snippets
            .FirstOrDefault(m => m.IdInImport == dto.IdInImport);
        if (missing is null)
            return Problems.BadRequest(
                $"'{dto.IdInImport}' is not in the missing snippets list",
                code: "snippet_not_missing");

        var args = JsonSerializer.SerializeToElement(new
        {
            id_in_import = missing.IdInImport,
            inferred_type = missing.InferredType,
            hint = dto.PromptHint,
            proposed_workflow = draft.Report.ProposedWorkflow,
        });

        var result = await handler.ExecuteAsync(args, ct);
        return Ok(result);
    }

    // ─── helpers ──────────────────────────────────────────────────────

    private static async Task WriteSseAsync(HttpResponse response, string type, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { type, data = payload });
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    // Filter out every node whose `snippet_id` is in `skipped` and drop
    // any edge whose `source` or `target` refers to one of those nodes.
    // The intent is a hard exclusion: the user told us they don't want
    // those snippets, so their nodes can't ride along either. Returns a
    // fresh JsonElement; `warnings` collects per-node notes so the user
    // sees which references were dropped at commit time.
    private static JsonElement DropSkippedNodes(
        JsonElement workflow, HashSet<string> skipped, List<string> warnings)
    {
        if (skipped.Count == 0) return workflow;
        if (workflow.ValueKind != JsonValueKind.Object) return workflow;

        // Pass 1: collect ids of nodes whose snippet_id was skipped.
        var droppedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        if (workflow.TryGetProperty("nodes", out var nodesEl)
            && nodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodesEl.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty("snippet_id", out var sidEl)
                    || sidEl.ValueKind != JsonValueKind.String) continue;
                var sid = sidEl.GetString() ?? "";
                if (!skipped.Contains(sid)) continue;
                var nid = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? ""
                    : "";
                if (nid.Length > 0)
                {
                    droppedNodeIds.Add(nid);
                    warnings.Add($"Dropped node '{nid}' and its edges because snippet '{sid}' was marked skip.");
                }
            }
        }

        if (droppedNodeIds.Count == 0) return workflow;

        // Pass 2: re-emit the workflow with nodes/edges filtered.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in workflow.EnumerateObject())
            {
                if (prop.NameEquals("nodes") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("nodes");
                    writer.WriteStartArray();
                    foreach (var node in prop.Value.EnumerateArray())
                    {
                        var nid = node.ValueKind == JsonValueKind.Object
                            && node.TryGetProperty("id", out var idEl)
                            && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? ""
                            : "";
                        if (droppedNodeIds.Contains(nid)) continue;
                        node.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else if (prop.NameEquals("edges") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("edges");
                    writer.WriteStartArray();
                    foreach (var edge in prop.Value.EnumerateArray())
                    {
                        if (edge.ValueKind != JsonValueKind.Object)
                        {
                            edge.WriteTo(writer);
                            continue;
                        }
                        var source = edge.TryGetProperty("source", out var srcEl)
                            && srcEl.ValueKind == JsonValueKind.String
                            ? srcEl.GetString() ?? ""
                            : "";
                        var target = edge.TryGetProperty("target", out var tgtEl)
                            && tgtEl.ValueKind == JsonValueKind.String
                            ? tgtEl.GetString() ?? ""
                            : "";
                        if (droppedNodeIds.Contains(source) || droppedNodeIds.Contains(target)) continue;
                        edge.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static JsonElement RewriteReferences(
        JsonElement workflow,
        Dictionary<string, Guid> snippetRemap,
        Dictionary<string, Guid> integrationRemap,
        Dictionary<string, (Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)> integrationActionRemap)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in workflow.EnumerateObject())
            {
                if (prop.NameEquals("nodes"))
                {
                    writer.WritePropertyName("nodes");
                    writer.WriteStartArray();
                    foreach (var node in prop.Value.EnumerateArray())
                    {
                        WriteRewrittenNode(writer, node, snippetRemap, integrationRemap, integrationActionRemap);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static void WriteRewrittenNode(
        Utf8JsonWriter writer,
        JsonElement node,
        Dictionary<string, Guid> snippetRemap,
        Dictionary<string, Guid> integrationRemap,
        Dictionary<string, (Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)> integrationActionRemap)
    {
        // Detect upfront whether this node's snippet_id matches an
        // integration_action-stub remap. If so, we both rewrite snippet_id
        // to the virtual sentinel AND splice integration_id / action_id /
        // names into config_overrides — even if the source node didn't
        // declare config_overrides at all.
        (Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)? integrationActionInfo = null;
        if (node.TryGetProperty("snippet_id", out var origSidEl)
            && origSidEl.ValueKind == JsonValueKind.String
            && integrationActionRemap.TryGetValue(origSidEl.GetString() ?? "", out var hit))
        {
            integrationActionInfo = hit;
        }

        var hasConfigOverrides = false;
        foreach (var p in node.EnumerateObject())
        {
            if (p.NameEquals("config_overrides")) { hasConfigOverrides = true; break; }
        }

        writer.WriteStartObject();
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.NameEquals("snippet_id") && prop.Value.ValueKind == JsonValueKind.String)
            {
                if (integrationActionInfo is not null)
                {
                    // Virtual sentinel — the validator + runtime dispatch via
                    // config_overrides, never via a Snippet lookup.
                    writer.WriteString("snippet_id", "integration_action");
                }
                else
                {
                    var sid = prop.Value.GetString() ?? "";
                    writer.WriteString("snippet_id",
                        snippetRemap.TryGetValue(sid, out var newId) ? newId.ToString() : sid);
                }
            }
            else if (prop.NameEquals("config_overrides") && prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("config_overrides");
                WriteRewrittenConfigOverrides(writer, prop.Value, integrationRemap, integrationActionInfo);
            }
            else
            {
                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }
        }
        // If the source node had no config_overrides but we're rewriting
        // it as an integration_action, synthesise one with the required
        // ids — the validator demands them.
        if (!hasConfigOverrides && integrationActionInfo is not null)
        {
            writer.WritePropertyName("config_overrides");
            WriteRewrittenConfigOverrides(writer, default, integrationRemap, integrationActionInfo);
        }
        writer.WriteEndObject();
    }

    // Emit config_overrides with:
    //   - integration_id remapped (or injected if the node is an
    //     integration_action stub).
    //   - action_id + integration_name + action_name injected when the
    //     node belongs to an integration_action stub remap.
    //   - every other field preserved verbatim.
    // Source can be `default` (no original config_overrides) — in that
    // case we only write the injected fields.
    private static void WriteRewrittenConfigOverrides(
        Utf8JsonWriter writer,
        JsonElement source,
        Dictionary<string, Guid> integrationRemap,
        (Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)? integrationActionInfo)
    {
        writer.WriteStartObject();

        // Track which keys the source already emitted so we don't double-
        // write the injected ones below.
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var coProp in source.EnumerateObject())
            {
                if (coProp.NameEquals("integration_id") && coProp.Value.ValueKind == JsonValueKind.String)
                {
                    var iid = coProp.Value.GetString() ?? "";
                    // integration_action stub wins over remap; remap is the
                    // fallback for the existing case where the YAML already
                    // carried an integration_id string we need to swap.
                    if (integrationActionInfo is not null)
                    {
                        writer.WriteString("integration_id", integrationActionInfo.Value.IntegrationId.ToString());
                    }
                    else
                    {
                        writer.WriteString("integration_id",
                            integrationRemap.TryGetValue(iid, out var newId) ? newId.ToString() : iid);
                    }
                    emitted.Add("integration_id");
                }
                else
                {
                    if (integrationActionInfo is not null
                        && (coProp.NameEquals("action_id")
                            || coProp.NameEquals("action_name")
                            || coProp.NameEquals("integration_name")))
                    {
                        // Skip — we'll write our resolved values below so the
                        // injected ones aren't shadowed by stale strings the
                        // YAML may have carried.
                        continue;
                    }
                    writer.WritePropertyName(coProp.Name);
                    coProp.Value.WriteTo(writer);
                    emitted.Add(coProp.Name);
                }
            }
        }

        if (integrationActionInfo is not null)
        {
            var info = integrationActionInfo.Value;
            if (!emitted.Contains("integration_id"))
                writer.WriteString("integration_id", info.IntegrationId.ToString());
            writer.WriteString("action_id", info.ActionId.ToString());
            writer.WriteString("integration_name", info.IntegrationName);
            writer.WriteString("action_name", info.ActionName);
        }

        writer.WriteEndObject();
    }

    private static string ResolveName(
        JsonElement workflow, string conflictRes, string? newName, NameCollision? collision)
    {
        // Our own exporter nests name/description under `workflow:`;
        // foreign translators may put them at the top level. Look in
        // both so the round-trip preserves the original title.
        var original = ReadWrappedString(workflow, "name") ?? "Imported workflow";
        return conflictRes switch
        {
            "rename" => string.IsNullOrWhiteSpace(newName) ? CopySuffix(original) : newName!,
            "fresh_copy" when collision is not null => CopySuffix(original),
            _ => original,
        };
    }

    // "original (copy 20260511-2037)" — short, sortable, unique enough
    // that two imports of the same workflow in the same minute still
    // collide cleanly via the seconds component.
    private static string CopySuffix(string original) =>
        $"{original} (copy {DateTime.UtcNow:yyyyMMdd-HHmmss})";

    // Materialise an Integration + IntegrationAction pair from a
    // generated_snippet whose declared type is `integration_action`.
    //
    // The agent typically packs integration metadata into the `code` field
    // (which makes no semantic sense for an integration_action, since
    // those are dispatched via config_overrides, not via runnable code).
    // We harvest `integration_name` + `action_name` (+ optional `method`)
    // from either the structured `code` object, the structured `code`
    // string parsed as JSON, or top-level fields on the payload. If we
    // can't find an integration name, we fall back to the catch-all.
    private async Task<(Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)>
        MaterialiseIntegrationActionFromGeneratedAsync(
            string idInImport,
            JsonElement generated,
            Func<Task<(Guid Id, string Name)>> ensureCatchAll)
    {
        // ── Extract metadata from the agent's payload ──────────────────
        var hints = ExtractGeneratedActionHints(generated);
        var rawIntegrationName = hints.IntegrationName;
        var actionName = string.IsNullOrWhiteSpace(hints.ActionName)
            ? idInImport
            : hints.ActionName!;
        var inferredMethod = string.IsNullOrWhiteSpace(hints.Method)
            ? InferHttpMethodFromActionName(actionName)
            : hints.Method!.ToUpperInvariant();

        // ── Resolve / create the parent integration ───────────────────
        Guid integrationId;
        string integrationName;
        if (!string.IsNullOrWhiteSpace(rawIntegrationName))
        {
            // Look up an existing integration by name (case-insensitive)
            // first — reuse before creating a duplicate.
            var existing = await _db.Integrations
                .AsNoTracking()
                .Where(i => i.IsActive
                            && i.Name.ToLower() == rawIntegrationName!.ToLower())
                .Select(i => new { i.IntegrationId, i.Name })
                .FirstOrDefaultAsync();

            if (existing is not null)
            {
                integrationId = existing.IntegrationId;
                integrationName = existing.Name;
            }
            else
            {
                // Stub the integration in needs_config so the workflow can
                // import; admin fills credentials/base URL afterwards.
                var fresh = new IntegrationModel
                {
                    IntegrationId = Guid.NewGuid(),
                    Name = rawIntegrationName!,
                    Type = "generic_rest",
                    BaseURL = "",
                    AuthConfig = JsonDocument.Parse("{}").RootElement.Clone(),
                    Headers = JsonDocument.Parse("{}").RootElement.Clone(),
                    HealthCheck = JsonDocument.Parse("{}").RootElement.Clone(),
                    Status = IntegrationStatus.NeedsConfig,
                    Enabled = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                _db.Integrations.Add(fresh);
                await _audit.LogAsync("integration", fresh.IntegrationId, "integration.created_needs_config",
                    after: new
                    {
                        integration_name = fresh.Name,
                        reason = "auto-created from AI-drafted integration_action during import",
                    });
                integrationId = fresh.IntegrationId;
                integrationName = fresh.Name;
            }
        }
        else
        {
            var (id, n) = await ensureCatchAll();
            integrationId = id;
            integrationName = n;
        }

        // ── Create the IntegrationAction row ──────────────────────────
        var actionRow = new flow_weaver_backend.Models.IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integrationId,
            Name = actionName,
            Method = inferredMethod,
            Path = "",
            Description = $"Auto-created from import (AI-drafted integration_action '{idInImport}'). Set the path + headers + body schema in /integrations/{integrationId} before running.",
            Category = "imported",
            IsActive = true,
            // Mirror Enabled with IsActive — see comment on the stub
            // branch above for why both are required by the palette query.
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.IntegrationActions.Add(actionRow);

        return (integrationId, actionRow.IntegrationActionId, integrationName, actionName);
    }

    // Resolve a missing integration_action snippet against an existing
    // integration that the user picked from the wizard's mapping
    // dropdown. The action is named after the import-side id so the
    // user can rename / re-path it later under /integrations/{id}. If
    // the chosen integration already has an action with that name we
    // re-use it (idempotent across re-imports).
    //
    // Returns (Guid.Empty, ...) when the target integration cannot be
    // found — the caller turns that into a 400 so the wizard can
    // surface a usable error instead of throwing.
    private async Task<(Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)>
        MapIntegrationActionToExistingIntegrationAsync(
            string idInImport, Guid targetIntegrationId, CancellationToken ct)
    {
        var integ = await _db.Integrations
            .AsNoTracking()
            .Where(i => i.IntegrationId == targetIntegrationId
                        && i.IsActive)
            .Select(i => new { i.IntegrationId, i.Name })
            .FirstOrDefaultAsync(ct);
        if (integ is null) return (Guid.Empty, Guid.Empty, "", "");

        var actionName = idInImport;

        // Re-use an existing action under this integration if its name
        // matches the import-side id (case-insensitive). Avoids
        // duplicating "notify_failure_stub" rows every time the same
        // workflow is re-imported.
        var existingAction = await _db.IntegrationActions
            .AsNoTracking()
            .Where(a => a.IntegrationId == integ.IntegrationId
                        && a.IsActive
                        && a.Name.ToLower() == actionName.ToLower())
            .Select(a => new { a.IntegrationActionId, a.Name })
            .FirstOrDefaultAsync(ct);
        if (existingAction is not null)
        {
            return (integ.IntegrationId, existingAction.IntegrationActionId, integ.Name, existingAction.Name);
        }

        var actionRow = new flow_weaver_backend.Models.IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integ.IntegrationId,
            Name = actionName,
            Method = InferHttpMethodFromActionName(actionName),
            Path = "",
            Description = $"Auto-created from import (mapped '{idInImport}' to integration '{integ.Name}'). Set the path + headers + body schema in /integrations/{integ.IntegrationId} before running.",
            Category = "imported",
            IsActive = true,
            // Mirror Enabled with IsActive — the palette query at
            // /api/integrationaction/actions/all filters by both, so
            // without this the node's action picker shows "no actions".
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.IntegrationActions.Add(actionRow);
        return (integ.IntegrationId, actionRow.IntegrationActionId, integ.Name, actionName);
    }

    private record GeneratedActionHints(string? IntegrationName, string? ActionName, string? Method);

    // Best-effort extraction of integration_name + action_name + method
    // from a generated_snippet payload. The agent has emitted at least
    // three shapes in the wild:
    //   1. `code` is a JSON object: { integration_name, action_name, params }
    //   2. `code` is a string containing JSON of shape (1)
    //   3. The metadata lives directly on the generated_snippet at the
    //      top level (alongside name/description).
    // Tries each in order. Anything missing simply stays null.
    private static GeneratedActionHints ExtractGeneratedActionHints(JsonElement generated)
    {
        string? integrationName = null;
        string? actionName = null;
        string? method = null;

        void Harvest(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            if (integrationName is null
                && obj.TryGetProperty("integration_name", out var inEl)
                && inEl.ValueKind == JsonValueKind.String)
                integrationName = inEl.GetString();
            if (actionName is null
                && obj.TryGetProperty("action_name", out var anEl)
                && anEl.ValueKind == JsonValueKind.String)
                actionName = anEl.GetString();
            if (method is null
                && obj.TryGetProperty("method", out var mEl)
                && mEl.ValueKind == JsonValueKind.String)
                method = mEl.GetString();
        }

        // Top-level on the generated_snippet itself.
        Harvest(generated);

        // `code` may be an object (the AI inlined the metadata as JSON)
        // or a string containing JSON. Try both.
        if (generated.ValueKind == JsonValueKind.Object
            && generated.TryGetProperty("code", out var codeEl))
        {
            if (codeEl.ValueKind == JsonValueKind.Object)
            {
                Harvest(codeEl);
            }
            else if (codeEl.ValueKind == JsonValueKind.String)
            {
                var raw = codeEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        Harvest(doc.RootElement);
                    }
                    catch (JsonException)
                    {
                        // `code` was free-form text — nothing structured to harvest.
                    }
                }
            }
        }

        // Strip obvious placeholder strings the agent emits when it
        // doesn't know the real value yet — treat them as "no hint".
        if (integrationName is not null
            && integrationName.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase))
            integrationName = null;
        if (actionName is not null
            && actionName.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase))
            actionName = null;

        return new GeneratedActionHints(integrationName, actionName, method);
    }

    private Snippet HydrateGeneratedSnippet(JsonElement payload)
    {
        var now = DateTime.UtcNow;
        return new Snippet
        {
            SnippetId = Guid.NewGuid(),
            Name = TryGetString(payload, "name") ?? $"generated_{Guid.NewGuid().ToString("N")[..8]}",
            Type = TryGetString(payload, "type") ?? "python_snippet",
            Description = TryGetString(payload, "description"),
            InputSchema = TryGetElement(payload, "input_schema"),
            OutputSchema = TryGetElement(payload, "output_schema"),
            Code = TryGetString(payload, "code"),
            ScriptLanguage = TryGetString(payload, "script_language"),
            TargetMode = TryGetString(payload, "target_mode") ?? "once",
            MaxParallel = 1,
            TimeoutSeconds = 60,
            Verified = false,
            RetryPolicy = JsonDocument.Parse("{}").RootElement.Clone(),
            CreatedBy = _caller.Username,
            Idempotency = null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static string? TryGetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // Our YAML/JSON exporter emits metadata under a `workflow:` wrapper
    // (`workflow.name`, `workflow.description`, `workflow.environment`).
    // Foreign translators put the same fields at the top level. Probe
    // the wrapper first because that's what our own round-trip produces;
    // fall back to the top level so n8n/itential/generic_dag still work.
    private static string? ReadWrappedString(JsonElement doc, string key)
    {
        if (doc.ValueKind == JsonValueKind.Object
            && doc.TryGetProperty("workflow", out var wrapper)
            && wrapper.ValueKind == JsonValueKind.Object)
        {
            var nested = TryGetString(wrapper, key);
            if (!string.IsNullOrWhiteSpace(nested)) return nested;
        }
        return TryGetString(doc, key);
    }

    // Auto-fabricate IntegrationAction rows for every `action_name`
    // referenced under an integration that the wizard just created
    // via `create_needs_config`. The runtime cannot dispatch an
    // integration_action without an `action_id`, and the imported
    // workflow only carries the action's name. Auto-creating the rows
    // (with an inferred HTTP method and empty path) gives the
    // downstream resolver something to match against and surfaces a
    // concrete TODO list under `/integrations/<id>/actions` for the
    // admin to flesh out.
    //
    // We only create actions for newly-created integrations: existing
    // integrations have their own action catalogue and the user is
    // expected to map the import to whatever's already there.
    private async Task<List<string>> AutoCreateIntegrationActionsAsync(
        JsonElement nodes,
        HashSet<Guid> newlyCreatedIntegrationIds,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        if (newlyCreatedIntegrationIds.Count == 0
            || nodes.ValueKind != JsonValueKind.Array) return warnings;

        var perIntegration = new Dictionary<Guid, HashSet<string>>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sid)
                || sid.ValueKind != JsonValueKind.String
                || sid.GetString() != "integration_action") continue;
            if (!node.TryGetProperty("config_overrides", out var co)
                || co.ValueKind != JsonValueKind.Object) continue;
            if (!co.TryGetProperty("integration_id", out var iidEl)
                || iidEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(iidEl.GetString(), out var iid)) continue;
            if (!newlyCreatedIntegrationIds.Contains(iid)) continue;
            if (!co.TryGetProperty("action_name", out var anEl)
                || anEl.ValueKind != JsonValueKind.String) continue;
            var name = anEl.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!perIntegration.TryGetValue(iid, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                perIntegration[iid] = set;
            }
            set.Add(name!);
        }

        if (perIntegration.Count == 0) return warnings;

        // Existing names per integration (in case the user is replaying
        // an import or some actions were already added manually).
        var existing = await _db.IntegrationActions
            .AsNoTracking()
            .Where(a => a.IsActive
                     && perIntegration.Keys.Contains(a.IntegrationId))
            .Select(a => new { a.IntegrationId, a.Name })
            .ToListAsync(ct);
        var existingSet = existing
            .GroupBy(a => a.IntegrationId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var created = 0;
        var now = DateTime.UtcNow;
        foreach (var (iid, names) in perIntegration)
        {
            existingSet.TryGetValue(iid, out var already);
            foreach (var name in names)
            {
                if (already is not null && already.Contains(name)) continue;
                _db.IntegrationActions.Add(new flow_weaver_backend.Models.IntegrationAction
                {
                    IntegrationActionId = Guid.NewGuid(),
                    IntegrationId = iid,
                    Name = name,
                    Method = InferHttpMethodFromActionName(name),
                    Path = "",
                    Description = "Auto-created during workflow import. Set the path + headers + body schema in /integrations/<id> before running.",
                    Category = "imported",
                    IsActive = true,
                    // The palette query filters by Enabled too — keep this
                    // mirrored with IsActive so the node's action picker can
                    // find the stub after import.
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                created++;
            }
        }

        if (created > 0)
        {
            // Flush so ResolveIntegrationActionsAsync's catalogue query
            // sees the new rows. The whole commit is wrapped in a tx
            // upstream, so a later failure still rolls these back.
            await _db.SaveChangesAsync(ct);
            warnings.Add(
                $"{created} integration action(s) auto-created on the new integration(s). " +
                "Open /integrations/<id> to set the path, headers, and request body schema before running the workflow.");
        }
        return warnings;
    }

    // Verb-based heuristic for HTTP method. Falls back to GET as the
    // safest option (idempotent, no body required) if no prefix matches.
    private static string InferHttpMethodFromActionName(string actionName)
    {
        var lower = actionName.ToLowerInvariant();
        if (StartsWithAny(lower, "get", "list", "find", "read", "show", "describe", "query", "search", "fetch", "load"))
            return "GET";
        if (StartsWithAny(lower, "create", "add", "post", "submit", "send", "publish", "trigger", "register"))
            return "POST";
        if (StartsWithAny(lower, "update", "modify", "edit", "set", "patch", "change"))
            return "PATCH";
        if (StartsWithAny(lower, "replace", "overwrite", "put"))
            return "PUT";
        if (StartsWithAny(lower, "delete", "remove", "destroy", "drop", "purge"))
            return "DELETE";
        return "GET";
    }

    private static bool StartsWithAny(string lower, params string[] prefixes)
    {
        foreach (var p in prefixes)
            if (lower.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    // Best-effort hydration of integration_action nodes after the
    // schema sanitiser runs. For every node with a real integration_id
    // GUID and an `action_name` string but no resolved `action_id`,
    // query (IntegrationId, Name) — case-insensitive — and
    // splice the resolved `action_id`, `method`, `path`, and
    // `integration_name` back into config_overrides so the editor
    // renders the endpoint correctly and the runtime can dispatch.
    //
    // Ambiguous names (two actions with the same Name under the same
    // integration) leave the node as-is and surface a warning the
    // wizard relays in the commit response. The user opens the node
    // in the editor and picks via the action dropdown.
    private async Task<(JsonElement Nodes, List<string> Warnings)> ResolveIntegrationActionsAsync(
        JsonElement nodes, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (nodes.ValueKind != JsonValueKind.Array) return (nodes, warnings);

        // Configured fuzzy-match thresholds. Pulled once at the top so
        // every node uses the same calibration even if the cache TTL
        // expires mid-loop.
        var importSettings = await _appSettings.GetAsync(ct);
        var fuzzyThreshold = importSettings.ImportFuzzyMatchThreshold;
        var fuzzyGap = importSettings.ImportFuzzyMatchGap;

        // Pass 1: collect every (integration_id, action_name) pair we
        // need to resolve so the lookup is a single query instead of N.
        var pairs = new HashSet<(Guid IntegrationId, string Name)>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!TryReadActionPair(node, out var integrationId, out var actionName, out _))
                continue;
            pairs.Add((integrationId, actionName));
        }
        if (pairs.Count == 0) return (nodes, warnings);

        var integrationIds = pairs.Select(p => p.IntegrationId).Distinct().ToArray();
        var candidates = await _db.IntegrationActions
            .AsNoTracking()
            .Where(a => a.IsActive
                     && integrationIds.Contains(a.IntegrationId))
            .Select(a => new
            {
                a.IntegrationActionId, a.IntegrationId, a.Name, a.Method, a.Path,
                a.Description,
            })
            .ToListAsync(ct);

        var integrationNames = await _db.Integrations
            .AsNoTracking()
            .Where(i => i.IsActive
                     && integrationIds.Contains(i.IntegrationId))
            .Select(i => new { i.IntegrationId, i.Name })
            .ToDictionaryAsync(i => i.IntegrationId, i => i.Name, ct);

        // Pass 2: emit nodes with action_id + method + path filled in
        // where we found a unique match. The output keeps the original
        // ordering of recognised fields.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                if (!TryReadActionPair(node, out var integrationId, out var actionName, out var nodeId))
                {
                    node.WriteTo(writer);
                    continue;
                }

                var actionsForIntegration = candidates
                    .Where(c => c.IntegrationId == integrationId)
                    .ToList();
                var matches = actionsForIntegration
                    .Where(c => string.Equals(c.Name, actionName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    // Phase 3: fall back to fuzzy matching the
                    // action_name against the catalogue. We score each
                    // candidate against its Name, Path, and
                    // Description and keep the best. Auto-apply only
                    // when (a) the best score is high enough and (b)
                    // the runner-up trails by a clear gap, so we don't
                    // accept a coin-flip.
                    var ranked = actionsForIntegration
                        .Select(c => new
                        {
                            Candidate = c,
                            Score = ScoreActionMatch(actionName, c.Name, c.Path, c.Description),
                        })
                        .OrderByDescending(x => x.Score)
                        .ToList();
                    var integrationLabel = integrationNames.TryGetValue(integrationId, out var n) ? n : integrationId.ToString();

                    if (ranked.Count > 0
                        && ranked[0].Score >= fuzzyThreshold
                        && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= fuzzyGap))
                    {
                        var picked = ranked[0].Candidate;
                        warnings.Add(
                            $"node '{nodeId}': action '{actionName}' fuzzy-matched to '{picked.Name}' " +
                            $"(score {ranked[0].Score:F2}). Review the node in the editor.");
                        WriteResolvedNode(writer, node, picked.IntegrationActionId, picked.Method, picked.Path,
                            integrationNames.TryGetValue(integrationId, out var iname) ? iname : null);
                        continue;
                    }

                    // Below the auto-apply threshold — include the
                    // top suggestion (if any plausible score) so the
                    // user has a concrete starting point in the editor
                    // instead of a flat "pick something" prompt.
                    var hint = ranked.Count > 0 && ranked[0].Score >= 0.4
                        ? $" Closest match: '{ranked[0].Candidate.Name}' (score {ranked[0].Score:F2})."
                        : "";
                    warnings.Add(
                        $"node '{nodeId}': action '{actionName}' not found under integration '{integrationLabel}'.{hint} " +
                        "Open the node in the editor and pick an action from the dropdown.");
                    node.WriteTo(writer);
                    continue;
                }
                if (matches.Count > 1)
                {
                    var integrationLabel = integrationNames.TryGetValue(integrationId, out var n) ? n : integrationId.ToString();
                    warnings.Add(
                        $"node '{nodeId}': action name '{actionName}' is ambiguous under integration '{integrationLabel}' " +
                        $"({matches.Count} matches). Pick the right one in the editor.");
                    node.WriteTo(writer);
                    continue;
                }

                var match = matches[0];
                WriteResolvedNode(writer, node, match.IntegrationActionId, match.Method, match.Path,
                    integrationNames.TryGetValue(integrationId, out var integrationName) ? integrationName : null);
            }
            writer.WriteEndArray();
        }
        return (JsonDocument.Parse(ms.ToArray()).RootElement.Clone(), warnings);
    }

    // Returns true if the node is an integration_action that has a
    // valid integration_id GUID + an action_name string but is missing
    // a resolved action_id. Out params capture the inputs the resolver
    // needs; `nodeId` is for the warning message.
    private static bool TryReadActionPair(
        JsonElement node,
        out Guid integrationId,
        out string actionName,
        out string nodeId)
    {
        integrationId = Guid.Empty;
        actionName = "";
        nodeId = "";
        if (node.ValueKind != JsonValueKind.Object) return false;
        if (!node.TryGetProperty("snippet_id", out var sid)
            || sid.ValueKind != JsonValueKind.String
            || sid.GetString() != "integration_action") return false;
        if (!node.TryGetProperty("config_overrides", out var co)
            || co.ValueKind != JsonValueKind.Object) return false;

        // If action_id is already a GUID, nothing to resolve.
        if (co.TryGetProperty("action_id", out var aid)
            && aid.ValueKind == JsonValueKind.String
            && Guid.TryParse(aid.GetString(), out _)) return false;

        if (!co.TryGetProperty("integration_id", out var iid)
            || iid.ValueKind != JsonValueKind.String
            || !Guid.TryParse(iid.GetString(), out integrationId)) return false;
        if (!co.TryGetProperty("action_name", out var an)
            || an.ValueKind != JsonValueKind.String) return false;
        actionName = an.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(actionName)) return false;

        nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? ""
            : "";
        return true;
    }

    // Re-emit a node with action_id / method / path / integration_name
    // resolved in config_overrides, AND redistribute the foreign
    // translator's bag-of-params into the runtime's canonical
    // `params` (path) / `query` (URL query) / `body` (request body)
    // buckets. Itential / n8n exports lump everything into a single
    // payload because their adapters split server-side; the FlowWeaver
    // IntegrationActionHandler reads the three keys separately, so we
    // distribute them here using the resolved method and path.
    //
    // Rules:
    //   - Names enclosed in `{…}` segments of `path` are PATH params.
    //   - For GET/DELETE/HEAD, the rest go to `query`.
    //   - For POST/PUT/PATCH (and unknown methods), the rest go to `body`.
    //   - Existing `params` / `query` / `body` keys on config_overrides
    //     are kept as authoritative — only the leftover top-level keys
    //     are redistributed. This preserves any prior manual edits.
    private static void WriteResolvedNode(
        Utf8JsonWriter writer,
        JsonElement node,
        Guid actionId,
        string method,
        string path,
        string? integrationName)
    {
        writer.WriteStartObject();
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.NameEquals("config_overrides")) continue;
            writer.WritePropertyName(prop.Name);
            prop.Value.WriteTo(writer);
        }
        writer.WritePropertyName("config_overrides");

        // Display-only keys preserved at the root of config_overrides.
        // The runtime ignores them; the editor's card uses them for the
        // node's subtitle / tooltip / hover details.
        var preservedRootKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "action_name", "summary", "description", "task_name", "view", "scheduled",
        };

        // Buckets the three runtime keys (`params`, `query`, `body`)
        // expect. Path-placeholder names go to `params`; for GET/DELETE/
        // HEAD the remainder lands in `query`; everything else
        // (POST/PUT/PATCH and unknown methods) lands in `body`. This
        // mirrors what foreign adapters do server-side and what the
        // FlowWeaver IntegrationActionHandler reads at run time.
        var pathParamNames = ParsePathParamNames(path);
        var paramsBucket = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var queryBucket = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var bodyBucket = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement? existingBody = null;
        string? originalIntegrationId = null;
        var rootExtras = new List<(string Name, JsonElement Value)>();
        var sendsBody = method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                     || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                     || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

        if (node.TryGetProperty("config_overrides", out var overrides)
            && overrides.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in overrides.EnumerateObject())
            {
                switch (prop.Name)
                {
                    // We re-emit these from the resolved arguments — skip the originals.
                    case "action_id" or "method" or "path" or "integration_name":
                        continue;
                    case "integration_id":
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            originalIntegrationId = prop.Value.GetString();
                        continue;
                    case "params" when prop.Value.ValueKind == JsonValueKind.Object:
                        foreach (var inner in prop.Value.EnumerateObject())
                            RouteValue(inner.Name, inner.Value);
                        break;
                    case "query" when prop.Value.ValueKind == JsonValueKind.Object:
                        foreach (var inner in prop.Value.EnumerateObject())
                            queryBucket[inner.Name] = inner.Value;
                        break;
                    case "body":
                        existingBody = prop.Value;
                        break;
                    default:
                        if (preservedRootKeys.Contains(prop.Name))
                            rootExtras.Add((prop.Name, prop.Value));
                        else
                            RouteValue(prop.Name, prop.Value);
                        break;
                }
            }
        }

        writer.WriteStartObject();
        // 1. Resolved identifiers go first so the JSON reads top-down
        //    as "what API + where".
        if (!string.IsNullOrEmpty(originalIntegrationId))
            writer.WriteString("integration_id", originalIntegrationId);
        if (!string.IsNullOrEmpty(integrationName))
            writer.WriteString("integration_name", integrationName);
        writer.WriteString("action_id", actionId.ToString());
        writer.WriteString("method", method);
        writer.WriteString("path", path);

        // 2. The three runtime buckets, only when non-empty so we don't
        //    pollute config_overrides with `params: {}` noise.
        if (paramsBucket.Count > 0)
        {
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            foreach (var kv in paramsBucket) { writer.WritePropertyName(kv.Key); kv.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        if (queryBucket.Count > 0)
        {
            writer.WritePropertyName("query");
            writer.WriteStartObject();
            foreach (var kv in queryBucket) { writer.WritePropertyName(kv.Key); kv.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        if (existingBody is { } b)
        {
            writer.WritePropertyName("body");
            b.WriteTo(writer);
        }
        else if (bodyBucket.Count > 0)
        {
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            foreach (var kv in bodyBucket) { writer.WritePropertyName(kv.Key); kv.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }

        // 3. Display-only extras (summary, description, …) at the root
        //    so the editor's card stays informative.
        foreach (var (name, value) in rootExtras)
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();

        void RouteValue(string name, JsonElement value)
        {
            if (pathParamNames.Contains(name))
            {
                paramsBucket[name] = value;
                return;
            }
            if (sendsBody) bodyBucket[name] = value;
            else queryBucket[name] = value;
        }
    }

    // Pulls `{name}` placeholder names out of an OpenAPI-style path.
    // We don't validate further (e.g. `{`name`}` allowed) because every
    // adapter we've seen uses the simple form.
    private static HashSet<string> ParsePathParamNames(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(path)) return set;
        var i = 0;
        while (i < path.Length)
        {
            var open = path.IndexOf('{', i);
            if (open < 0) break;
            var close = path.IndexOf('}', open + 1);
            if (close < 0) break;
            var name = path[(open + 1)..close];
            if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
            i = close + 1;
        }
        return set;
    }

    // Phase 3: rank an IntegrationAction candidate against the
    // imported action_name. We score against Name (highest weight),
    // Path tail (for cases like "getDevices" vs "/api/dcim/devices"),
    // and Description (cheap last-chance signal). The blended score
    // is in [0, 1]; the caller's threshold (0.8) decides whether to
    // auto-apply.
    private static double ScoreActionMatch(string actionName, string name, string path, string? description)
    {
        var nameScore = NameSimilarity(actionName, name);
        var pathTail = ExtractPathTail(path);
        var pathScore = pathTail.Length > 0 ? NameSimilarity(actionName, pathTail) : 0.0;
        var descScore = !string.IsNullOrWhiteSpace(description)
            ? NameSimilarity(actionName, description!.Length > 64 ? description[..64] : description)
            : 0.0;
        return nameScore * 0.7 + pathScore * 0.25 + descScore * 0.05;
    }

    private static string ExtractPathTail(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    // Levenshtein-derived similarity in [0, 1]. Mirror of the helper
    // in DependencyResolver — kept local so the controller doesn't
    // pull in a service dependency just for one string distance call.
    private static double NameSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1.0;
        if (a.Length > 64) a = a[..64];
        if (b.Length > 64) b = b[..64];
        var dist = Levenshtein(a, b);
        var max = Math.Max(a.Length, b.Length);
        return 1.0 - (double)dist / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var m = a.Length;
        var n = b.Length;
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (var j = 0; j <= n; j++) prev[j] = j;
        for (var i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[n];
    }

    // Strict-schema sanitizer for nodes. Imports come from many sources
    // (our own export, n8n, Itential, AI). They routinely carry stray
    // keys (`label`, `position`, `data`) and out-of-enum `type` values
    // (`start`, `end`) that the v1 JSON Schema rejects via
    // `additionalProperties: false`. Rather than relax the schema or
    // patch every translator, we keep one whitelisting pass right
    // before validation. The output preserves the original ordering of
    // recognised fields and drops everything else.
    private static readonly HashSet<string> AllowedNodeTypes =
        new(StringComparer.Ordinal) { "task", "decision", "subflow" };

    private static JsonElement NormalizeNodesForV1Schema(JsonElement nodes)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return nodes.Clone();

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                writer.WriteStartObject();
                if (node.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in node.EnumerateObject())
                    {
                        switch (prop.Name)
                        {
                            case "id":
                            case "snippet_id":
                                if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    writer.WriteString(prop.Name, prop.Value.GetString());
                                }
                                break;
                            case "x":
                            case "y":
                                if (prop.Value.ValueKind == JsonValueKind.Number)
                                {
                                    writer.WritePropertyName(prop.Name);
                                    prop.Value.WriteTo(writer);
                                }
                                break;
                            case "config_overrides":
                                writer.WritePropertyName("config_overrides");
                                if (prop.Value.ValueKind == JsonValueKind.Object)
                                {
                                    prop.Value.WriteTo(writer);
                                }
                                else
                                {
                                    writer.WriteStartObject();
                                    writer.WriteEndObject();
                                }
                                break;
                            case "type":
                                // Only forward the field when the value is one
                                // of the enum members; otherwise drop it so
                                // the optional default ("task") applies.
                                if (prop.Value.ValueKind == JsonValueKind.String
                                    && AllowedNodeTypes.Contains(prop.Value.GetString() ?? ""))
                                {
                                    writer.WriteString("type", prop.Value.GetString());
                                }
                                break;
                            // Any other key (`label`, `data`, `position`,
                            // `width`, `height`, `selected`, …) is silently
                            // dropped.
                        }
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static readonly HashSet<string> AllowedEdgeTypes =
        new(StringComparer.Ordinal) { "success", "failure", "always", "conditional" };

    private static JsonElement NormalizeEdgesForV1Schema(JsonElement edges)
    {
        if (edges.ValueKind != JsonValueKind.Array) return edges.Clone();

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var edge in edges.EnumerateArray())
            {
                writer.WriteStartObject();
                if (edge.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in edge.EnumerateObject())
                    {
                        switch (prop.Name)
                        {
                            case "source":
                            case "target":
                                if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    writer.WriteString(prop.Name, prop.Value.GetString());
                                }
                                break;
                            case "type":
                                if (prop.Value.ValueKind == JsonValueKind.String
                                    && AllowedEdgeTypes.Contains(prop.Value.GetString() ?? ""))
                                {
                                    writer.WriteString("type", prop.Value.GetString());
                                }
                                break;
                            case "condition":
                            case "source_handle":
                            case "target_handle":
                                // Schema wants `string`; YAML round-trips
                                // commonly produce null for optional fields
                                // the exporter wrote as `condition:` with
                                // no value. Drop nulls / non-strings so the
                                // optional fields stay truly optional.
                                if (prop.Value.ValueKind == JsonValueKind.String
                                    && !string.IsNullOrEmpty(prop.Value.GetString()))
                                {
                                    writer.WriteString(prop.Name, prop.Value.GetString());
                                }
                                break;
                        }
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static JsonElement TryGetElement(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        if (!obj.TryGetProperty(key, out var v)) return default;
        return v.Clone();
    }

    // FU-5: shallow merge of two metadata objects with the imported
    // side winning on key collisions. We don't go deeper than one level
    // because metadata values are typically scalars or flags
    // (`is_subflow`, `source_format`); deep-merge with overwrite policy
    // would surprise authors who set nested defaults.
    private static JsonElement MergeMetadata(JsonElement existing, JsonElement imported)
    {
        if (imported.ValueKind != JsonValueKind.Object) return existing;
        if (existing.ValueKind != JsonValueKind.Object) return imported;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            // Existing keys first; imported keys overwrite if the name matches.
            var importedKeys = new HashSet<string>(
                imported.EnumerateObject().Select(p => p.Name),
                StringComparer.Ordinal);
            foreach (var p in existing.EnumerateObject())
            {
                if (importedKeys.Contains(p.Name)) continue;
                writer.WritePropertyName(p.Name);
                p.Value.WriteTo(writer);
            }
            foreach (var p in imported.EnumerateObject())
            {
                writer.WritePropertyName(p.Name);
                p.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}

public sealed class CommitImportRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("conflict_resolution")]
    public string? ConflictResolution { get; set; } // rename | replace | keep_existing | fresh_copy

    [System.Text.Json.Serialization.JsonPropertyName("new_name")]
    public string? NewName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("target_environment")]
    public string? TargetEnvironment { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("duplicate_action")]
    public string? DuplicateAction { get; set; } // skip | update_existing | import_as_new

    [System.Text.Json.Serialization.JsonPropertyName("snippets")]
    public Dictionary<string, ResolvedDependency> Snippets { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("integrations")]
    public Dictionary<string, ResolvedDependency> Integrations { get; set; } = new();
}

public sealed class ResolvedDependency
{
    [System.Text.Json.Serialization.JsonPropertyName("action")]
    public string Action { get; set; } = "stub";

    [System.Text.Json.Serialization.JsonPropertyName("target_id")]
    public Guid? TargetId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("generated_snippet")]
    public JsonElement? GeneratedSnippet { get; set; }
}

public sealed class GenerateSnippetRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("id_in_import")]
    public string IdInImport { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("prompt_hint")]
    public string? PromptHint { get; set; }
}
