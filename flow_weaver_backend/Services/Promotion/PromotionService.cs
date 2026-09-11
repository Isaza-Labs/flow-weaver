using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimulationResultModel = flow_weaver_backend.Models.SimulationResult;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowVersionModel = flow_weaver_backend.Models.WorkflowVersion;

namespace flow_weaver_backend.Services.Promotion;

// Governs the draft → qa → production lifecycle. Each promotion creates a
// WorkflowVersion snapshot of the current state (immutable audit trail)
// and a NEW Workflow row with the target environment. The original row
// stays untouched so the draft workspace is never lost.
//
// The path is:  draft → qa → production
// Rollback clones a prior WorkflowVersion back into a draft.
public class PromotionService : IPromotionService
{
    private static readonly string[] PromotionPath = { "draft", "qa", "production" };

    private readonly IWorkflowRepository _workflows;
    private readonly IWorkflowVersionRepository _versions;
    private readonly IRepository<SimulationResultModel> _simulations;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly IPolicyEvaluator _policyEvaluator;
    private readonly WorkflowRollbackAnalyzer _rollbackAnalyzer;
    private readonly IEffectivePermissions _effective;
    private readonly IAppSettingsService _settings;
    private readonly ILogger<PromotionService> _logger;

    public PromotionService(
        IWorkflowRepository workflows,
        IWorkflowVersionRepository versions,
        IRepository<SimulationResultModel> simulations,
        IUnitOfWork uow,
        ICurrentUser caller,
        IAuditLogger audit,
        ITraceLogger trace,
        IPolicyEvaluator policyEvaluator,
        WorkflowRollbackAnalyzer rollbackAnalyzer,
        IEffectivePermissions effective,
        IAppSettingsService settings,
        ILogger<PromotionService> logger)
    {
        _workflows = workflows;
        _versions = versions;
        _simulations = simulations;
        _uow = uow;
        _caller = caller;
        _audit = audit;
        _trace = trace;
        _policyEvaluator = policyEvaluator;
        _rollbackAnalyzer = rollbackAnalyzer;
        _effective = effective;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ActionResult<WorkflowResponse>> PromoteAsync(
        Guid workflowId, PromoteRequest req, CancellationToken ct)
    {
        var wf = await FindWorkflow(workflowId, ct);
        if (wf is null)
        {
            _logger.LogWarning("promotion.promote.not_found workflow_id={WorkflowId}", workflowId);
            return Err<WorkflowResponse>(404, "workflow not found");
        }

        var target = req.TargetEnvironment?.ToLowerInvariant() ?? string.Empty;
        if (target is not ("qa" or "production"))
        {
            _logger.LogWarning("promotion.promote.validation_failed reason=target_environment_invalid target={Target}", target);
            return Err<WorkflowResponse>(400, "target_environment must be 'qa' or 'production'");
        }

        var currentIdx = Array.IndexOf(PromotionPath, wf.Environment);
        var targetIdx = Array.IndexOf(PromotionPath, target);
        if (targetIdx <= currentIdx)
        {
            _logger.LogWarning(
                "promotion.promote.validation_failed reason=invalid_path from={FromEnv} to={ToEnv}",
                wf.Environment, target);
            return Err<WorkflowResponse>(400, $"cannot promote from '{wf.Environment}' to '{target}'");
        }

        if (target == "production" && string.IsNullOrWhiteSpace(req.ApprovedBy))
        {
            _logger.LogWarning("promotion.promote.validation_failed reason=approved_by_required workflow_id={WorkflowId}", workflowId);
            return Err<WorkflowResponse>(400, "promotion to production requires approved_by");
        }

        if (target == "production" && req.ApprovedBy == req.PromotedBy)
        {
            _logger.LogWarning("promotion.promote.validation_failed reason=approver_equals_promoter workflow_id={WorkflowId}", workflowId);
            return Err<WorkflowResponse>(400, "approved_by must differ from promoted_by");
        }

        // S16 — Harness lesson 9 ("no victory until verified") gate:
        // draft→qa now requires a successful simulation whose schema
        // hash still matches the current DAG. WorkflowService.UpdateAsync
        // clears LastSimulationId on any structural edit so this gate
        // catches authors who simulated, then changed the graph, then
        // tried to promote.
        if (target == "qa")
        {
            var simGate = await CheckSimulationGateAsync(wf, ct);
            if (!simGate.Ok)
            {
                _logger.LogWarning(
                    "promotion.promote.validation_failed reason={Reason} workflow_id={WorkflowId}",
                    simGate.Reason, workflowId);
                return Err<WorkflowResponse>(412, simGate.Message, code: simGate.Reason);
            }
        }

        // Granular RBAC (plan_rbac_granular.md §6.2): in granular mode the
        // caller must hold workflow.promote conditioned on the TARGET
        // environment (and this workflow). The coarse [HasPermission] on the
        // controller only checked "can promote at all"; this enforces the
        // env/resource condition so a "promote to qa only" grant can't reach
        // production. admin bypasses inside EffectivePermissions.
        if (RbacModes.IsGranular((await _settings.GetAsync(ct)).RbacMode))
        {
            var permitted = await _effective.HasAsync(
                "workflow.promote",
                new PermissionContext(
                    Environment: target,
                    ResourceType: "workflow",
                    ResourceId: wf.WorkflowId),
                ct);
            if (!permitted)
            {
                _logger.LogWarning(
                    "promotion.promote.permission_denied workflow_id={WorkflowId} target={Target} user_id={UserId}",
                    workflowId, target, _caller.UserId);
                return Err<WorkflowResponse>(
                    403, $"you do not have permission to promote to {target}", code: "permission_denied");
            }
        }

        // FR-021 + Phase-3 gates: delegate the qa→production "must have a
        // recent successful run" check (and any other admin-defined
        // promotion gate) to the policy evaluator. The seeded default
        // gate keeps the 48h rule operational without admin work; admins
        // can replace or extend it via /policies.
        var policyDecision = await _policyEvaluator.EvaluateAsync(new PolicyEvaluationContext(
                Action: "promote",
                Environment: wf.Environment,
                WorkflowName: wf.Name,
                WorkflowDescription: wf.Description,
                Nodes: wf.Nodes,
                DeviceRoles: Array.Empty<string>(),
                DevicePoolNames: Array.Empty<string>(),
                WorkflowId: wf.WorkflowId,
                TargetEnvironment: target),
            ct);
        if (!policyDecision.Allowed)
        {
            _logger.LogWarning(
                "promotion.promote.policy_blocked workflow_id={WorkflowId} policy={PolicyName} reason={Reason}",
                workflowId, policyDecision.PolicyName, policyDecision.Reason);
            await _trace.EventAsync("policy.blocked", "workflow", "denied",
                metadata: new
                {
                    workflow_id = workflowId,
                    policy_name = policyDecision.PolicyName,
                    action = "promote",
                    reason = policyDecision.Reason,
                    from_environment = wf.Environment,
                    to_environment = target,
                },
                ct: ct);
            return Err<WorkflowResponse>(412, policyDecision.Reason ?? "promotion blocked by policy");
        }

        var now = DateTime.UtcNow;

        // Snapshot the current state as a WorkflowVersion, keyed on the version
        // being promoted — "this is what v1 looked like when it shipped".
        //
        // `workflow_versions` carries a UNIQUE (WorkflowId, Version). Nothing
        // else moves Workflow.Version — editing a workflow leaves it alone — so
        // without the bump below, a second promotion of the same row rebuilds
        // the identical pair and Postgres answers 23505. Promoting twice is an
        // ordinary thing to do (fix the draft and promote again; ship qa to
        // production again), so the source advances a revision on every
        // promotion and the next snapshot lands on a free number.
        var version = new WorkflowVersionModel
        {
            WorkflowVersionId = Guid.NewGuid(),
            WorkflowId = wf.WorkflowId,
            Version = wf.Version,
            Nodes = wf.Nodes,
            Edges = wf.Edges,
            Services = default,
            ChangeSummary = req.ChangeSummary,
            PromotedAt = now,
            PromotedBy = req.PromotedBy ?? _caller.Username ?? string.Empty,
            ConversationId = wf.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _versions.Add(version);

        // Create the promoted workflow copy.
        var promoted = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = wf.Name,
            Description = wf.Description,
            Version = wf.Version,
            SchemaVersion = wf.SchemaVersion,
            InputSchema = wf.InputSchema,
            Nodes = wf.Nodes,
            Edges = wf.Edges,
            Metadata = wf.Metadata,
            Environment = target,
            PromotedFrom = wf.WorkflowId,
            ChangeSummary = req.ChangeSummary,
            PromotedAt = now,
            ConversationId = wf.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _workflows.Add(promoted);

        // Advance the source so the next promotion snapshots a free version.
        // The promoted copy keeps the version it corresponds to, above.
        wf.Version += 1;
        wf.UpdatedAt = now;

        try
        {
            await _uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two promotions of the same workflow racing each other read the
            // same Version before either committed. The loser gets a clean 409
            // instead of a 500 — the operation is retryable, and the caller
            // needs to know its promotion is not the one that landed.
            _logger.LogWarning(
                ex, "promotion.promote.version_conflict workflow_id={WorkflowId} version={Version}",
                workflowId, version.Version);
            return Err<WorkflowResponse>(409,
                "another promotion of this workflow completed first. Reload and try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "promotion.promote.failed workflow_id={WorkflowId}", workflowId);
            throw;
        }

        await _audit.LogAsync("workflow", promoted.WorkflowId, "promote",
            before: new { from_environment = wf.Environment, from_workflow_id = wf.WorkflowId },
            after: new { to_environment = target, version = promoted.Version, approved_by = req.ApprovedBy, promoted_by = req.PromotedBy },
            ct: ct);
        await _trace.EventAsync("workflow.promote", "workflow", "completed",
            metadata: new { workflow_id = promoted.WorkflowId, from_environment = wf.Environment, to_environment = target, version = promoted.Version },
            ct: ct);

        _logger.LogInformation(
            "promotion.promote.ok workflow_id={WorkflowId} new_workflow_id={NewWorkflowId} from={FromEnv} to={ToEnv} version={Version}",
            workflowId, promoted.WorkflowId, wf.Environment, target, promoted.Version);

        return new ObjectResult(ToResponse(promoted)) { StatusCode = 201 };
    }

    public async Task<ActionResult<WorkflowResponse>> RollbackAsync(
        Guid workflowId, int toVersion, CancellationToken ct)
    {
        var wf = await FindWorkflow(workflowId, ct);
        if (wf is null)
        {
            _logger.LogWarning("promotion.rollback.not_found workflow_id={WorkflowId}", workflowId);
            return Err<WorkflowResponse>(404, "workflow not found");
        }

        var snapshot = await _versions.GetByWorkflowAndVersionAsync(workflowId, toVersion, ct);
        if (snapshot is null)
        {
            _logger.LogWarning(
                "promotion.rollback.not_found workflow_id={WorkflowId} version={Version} reason=snapshot_missing",
                workflowId, toVersion);
            return Err<WorkflowResponse>(404, $"version {toVersion} not found for this workflow");
        }

        // S13.6 — refuse rollback when the snapshot graph contains
        // non-reversible nodes. Authors must rebuild from draft instead
        // of pretending to "undo" something whose effect can't be undone.
        var report = await AnalyzeSnapshotAsync(snapshot, ct);
        if (report.NonReversible.Count > 0)
        {
            var blocking = string.Join(", ",
                report.NonReversible.Select(r => $"{r.SnippetName}({r.SnippetType})"));
            _logger.LogWarning(
                "promotion.rollback.blocked workflow_id={WorkflowId} version={Version} non_reversible_count={Count}",
                workflowId, toVersion, report.NonReversible.Count);
            await _audit.LogAsync("workflow", workflowId, "rollback.blocked",
                before: new { from_version = wf.Version },
                after: new
                {
                    to_version = toVersion,
                    blocking_nodes = report.NonReversible.Select(r => new
                    {
                        snippet_id = r.SnippetId,
                        snippet_name = r.SnippetName,
                        snippet_type = r.SnippetType,
                    }).ToArray(),
                },
                ct: ct);
            return Err<WorkflowResponse>(409,
                $"rollback blocked: workflow contains non-reversible step(s): {blocking}. Build a forward fix in a new draft instead.");
        }

        var now = DateTime.UtcNow;
        var rolled = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = wf.Name,
            Description = wf.Description,
            Version = wf.Version + 1,
            SchemaVersion = wf.SchemaVersion,
            InputSchema = wf.InputSchema,
            Nodes = snapshot.Nodes,
            Edges = snapshot.Edges,
            Metadata = wf.Metadata,
            Environment = "draft",
            PromotedFrom = wf.WorkflowId,
            ChangeSummary = $"Rolled back to version {toVersion}",
            ConversationId = wf.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _workflows.Add(rolled);
        try
        {
            await _workflows.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "promotion.rollback.failed workflow_id={WorkflowId} version={Version}", workflowId, toVersion);
            throw;
        }

        await _audit.LogAsync("workflow", rolled.WorkflowId, "rollback",
            before: new { from_version = wf.Version, from_workflow_id = wf.WorkflowId },
            after: new { to_version = toVersion, new_workflow_id = rolled.WorkflowId },
            ct: ct);
        await _trace.EventAsync("workflow.rollback", "workflow", "completed",
            metadata: new { workflow_id = rolled.WorkflowId, to_version = toVersion },
            ct: ct);

        _logger.LogInformation(
            "promotion.rollback.ok workflow_id={WorkflowId} new_workflow_id={NewWorkflowId} to_version={ToVersion}",
            workflowId, rolled.WorkflowId, toVersion);

        return ToResponse(rolled);
    }

    public async Task<ActionResult<DiffResult>> DiffAsync(Guid workflowId, CancellationToken ct)
    {
        var wf = await FindWorkflow(workflowId, ct);
        if (wf is null)
        {
            _logger.LogWarning("promotion.diff.not_found workflow_id={WorkflowId}", workflowId);
            return Err<DiffResult>(404, "workflow not found");
        }

        // Find the most recent promoted copy to diff against. If none,
        // every node/edge is "added".
        var promoted = await _workflows.GetLatestPromotedFromAsync(workflowId, ct);

        var oldNodes = ParseNodeMap(promoted?.Nodes ?? default);
        var newNodes = ParseNodeMap(wf.Nodes);
        var oldEdges = ParseEdgeList(promoted?.Edges ?? default);
        var newEdges = ParseEdgeList(wf.Edges);

        var added = newNodes.Where(kv => !oldNodes.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var removed = oldNodes.Where(kv => !newNodes.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var changed = new List<NodeChange>();
        foreach (var (id, newEl) in newNodes)
        {
            if (!oldNodes.TryGetValue(id, out var oldEl)) continue;
            if (oldEl.GetRawText() != newEl.GetRawText())
                changed.Add(new NodeChange { NodeId = id, Before = oldEl, After = newEl });
        }

        var edgesAdded = newEdges.Where(e => !oldEdges.Contains(e)).Select(e => JsonDocument.Parse(e).RootElement).ToList();
        var edgesRemoved = oldEdges.Where(e => !newEdges.Contains(e)).Select(e => JsonDocument.Parse(e).RootElement).ToList();

        var result = new DiffResult
        {
            NodesAdded = added,
            NodesRemoved = removed,
            NodesChanged = changed,
            EdgesAdded = edgesAdded,
            EdgesRemoved = edgesRemoved,
            HasChanges = added.Count > 0 || removed.Count > 0 || changed.Count > 0
                         || edgesAdded.Count > 0 || edgesRemoved.Count > 0,
        };
        return result;
    }

    public async Task<ActionResult<WorkflowResponse>> CloneAsync(Guid workflowId, CancellationToken ct)
    {
        var wf = await FindWorkflow(workflowId, ct);
        if (wf is null)
        {
            _logger.LogWarning("promotion.clone.not_found workflow_id={WorkflowId}", workflowId);
            return Err<WorkflowResponse>(404, "workflow not found");
        }

        var now = DateTime.UtcNow;
        var clone = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = $"{wf.Name} (copy)",
            Description = wf.Description,
            Version = 1,
            SchemaVersion = wf.SchemaVersion,
            InputSchema = wf.InputSchema,
            Nodes = wf.Nodes,
            Edges = wf.Edges,
            Metadata = wf.Metadata,
            Environment = "draft",
            ChangeSummary = $"Cloned from {wf.WorkflowId}",
            ConversationId = wf.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _workflows.Add(clone);
        try
        {
            await _workflows.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "promotion.clone.failed workflow_id={WorkflowId}", workflowId);
            throw;
        }

        _logger.LogInformation(
            "promotion.clone.ok workflow_id={WorkflowId} new_workflow_id={NewWorkflowId}",
            workflowId, clone.WorkflowId);

        return new ObjectResult(ToResponse(clone)) { StatusCode = 201 };
    }

    // ─── helpers ────────────────────────────────────────────────────

    private async Task<WorkflowModel?> FindWorkflow(Guid id, CancellationToken ct) =>
        await _workflows.GetByIdAsync(id, ct: ct);

    // Postgres surfaces unique-violation as Npgsql.PostgresException with
    // SqlState 23505. Probed by reflection to avoid a hard Npgsql dependency
    // at this layer — same idiom as ResourcePermissionRepository, and it keeps
    // the InMemory test path callable without a Postgres reference.
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            var name = inner.GetType().FullName ?? string.Empty;
            if (name.Contains("PostgresException", StringComparison.Ordinal))
            {
                var sqlState = inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
                if (string.Equals(sqlState, "23505", StringComparison.Ordinal)) return true;
            }
            inner = inner.InnerException;
        }
        return false;
    }

    private async Task<SimulationGateResult> CheckSimulationGateAsync(WorkflowModel wf, CancellationToken ct)
    {
        if (wf.LastSimulationId is null)
        {
            return Decide(simulationExists: false, simulationOk: false, simulationHash: null,
                currentHash: null, issueCount: 0);
        }

        var sim = await _simulations.GetByIdAsync(wf.LastSimulationId.Value, tracking: false, ct: ct);
        if (sim is null)
        {
            // A purged row is the same OUTCOME as never having simulated, with a different
            // remedy — hence the distinct message and the shared reason code.
            return Decide(simulationExists: false, simulationOk: false, simulationHash: null,
                currentHash: null, issueCount: 0) with
            {
                Message = "the workflow's last simulation row is gone (purged). " +
                    "Re-run simulate_workflow_run.",
            };
        }

        return Decide(
            simulationExists: true,
            simulationOk: sim.Ok,
            simulationHash: sim.SchemaHash,
            currentHash: Services.Workflow.WorkflowCanonicalizer.ComputeSchemaHash(wf.Nodes, wf.Edges),
            issueCount: sim.IssueCount);
    }

    /// <summary>
    /// The draft→qa simulation gate, as a decision over values rather than over a database.
    /// </summary>
    /// <remarks>
    /// Extracted so the conformance adapter asks the REAL rule instead of restating it. The
    /// ORDER is the substance: missing before failed before stale, because a stale check
    /// against a simulation that failed anyway would report the wrong remedy, and an author
    /// told to re-simulate a graph they never simulated learns nothing.
    /// </remarks>
    internal static SimulationGateResult Decide(
        bool simulationExists, bool simulationOk, string? simulationHash, string? currentHash, int issueCount)
    {
        if (!simulationExists)
            return new(false, "simulation_missing",
                "draft→qa requires a successful simulate_workflow_run. " +
                "Run the simulator before promoting.");

        if (!simulationOk)
            return new(false, "simulation_failed",
                $"the last simulation reported {issueCount} structural issue(s). " +
                "Fix them and re-simulate before promoting.");

        if (!string.Equals(currentHash, simulationHash, StringComparison.OrdinalIgnoreCase))
            return new(false, "simulation_stale",
                "the workflow has changed since the last simulation. Re-run simulate_workflow_run.");

        return new(true, "ok", string.Empty);
    }

    internal sealed record SimulationGateResult(bool Ok, string Reason, string Message);

    // Wraps a WorkflowVersion into a Workflow-shaped projection so the
    // analyzer can read the snapshot's graph without a second model.
    private Task<WorkflowRollbackReport> AnalyzeSnapshotAsync(
        WorkflowVersionModel snapshot, CancellationToken ct)
    {
        var projection = new WorkflowModel
        {
            Nodes = snapshot.Nodes,
            Edges = snapshot.Edges,
        };
        return _rollbackAnalyzer.AnalyzeAsync(projection, ct);
    }

    // Internal helper that mirrors the Problems.* shape so promotion
    // failures land in the frontend the same way as controller-level
    // validation failures (top-level `error` + extensions `code`).
    //
    // 404 takes the raw message as Detail (not as a resource label) to
    // preserve existing PromotionService messages like "workflow not
    // found" verbatim; Problems.NotFound(resource) is for fresh sites
    // that prefer "<resource> '<key>' not found" formatting.
    private static ActionResult<T> Err<T>(int status, string error, string? code = null) =>
        status switch
        {
            400 => new ActionResult<T>(Problems.BadRequest(error, code)),
            403 => new ActionResult<T>(Problems.Forbidden(error, code)),
            404 => new ActionResult<T>(BuildProblem(404, "Not Found", error,
                code ?? "not_found",
                "https://flow-weaver.io/errors/not-found")),
            409 => new ActionResult<T>(Problems.Conflict(error, code)),
            412 => new ActionResult<T>(Problems.PreconditionFailed(error, code)),
            413 => new ActionResult<T>(Problems.PayloadTooLarge(error, code)),
            _ => new ActionResult<T>(BuildProblem(status, "Error", error,
                code ?? "error", "about:blank")),
        };

    private static ObjectResult BuildProblem(int status, string title, string detail, string code, string type)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = type,
        };
        problem.Extensions["error"] = detail;
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private static Dictionary<string, JsonElement> ParseNodeMap(JsonElement nodes)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return new();
        var map = new Dictionary<string, JsonElement>();
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                map[idEl.GetString()!] = n;
        }
        return map;
    }

    private static HashSet<string> ParseEdgeList(JsonElement edges)
    {
        if (edges.ValueKind != JsonValueKind.Array) return new();
        return edges.EnumerateArray().Select(e => e.GetRawText()).ToHashSet();
    }

    private static WorkflowResponse ToResponse(WorkflowModel w) => new()
    {
        WorkflowId = w.WorkflowId,
        Name = w.Name,
        Description = w.Description,
        Version = w.Version,
        SchemaVersion = w.SchemaVersion,
        InputSchema = w.InputSchema,
        Nodes = w.Nodes,
        Edges = w.Edges,
        Metadata = w.Metadata,
        CreatedBy = w.CreatedBy,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
        Environment = w.Environment,
        PromotedFrom = w.PromotedFrom,
        ChangeSummary = w.ChangeSummary,
        PromotedAt = w.PromotedAt,
        ConversationId = w.ConversationId,
    };
}
