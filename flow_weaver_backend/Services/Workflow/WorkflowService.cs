using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.Mvc;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Workflow;

public class WorkflowService : IWorkflow
{
    private const string DefaultEnvironment = "draft";

    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ICurrentUser _caller;
    private readonly IWorkflowSchemaValidator _schemaValidator;
    private readonly IWorkflowReferenceValidator _referenceValidator;
    private readonly IVendorCommandValidator _vendorCommandValidator;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly IPolicyEvaluator _policies;
    private readonly IResourcePermissionService _permissions;
    private readonly flow_weaver_backend.Services.Settings.IAppSettingsService _appSettings;
    private readonly ILogger<WorkflowService> _logger;

    public WorkflowService(
        IRepository<WorkflowModel> workflows,
        ICurrentUser caller,
        IWorkflowSchemaValidator schemaValidator,
        IWorkflowReferenceValidator referenceValidator,
        IVendorCommandValidator vendorCommandValidator,
        IAuditLogger audit,
        ITraceLogger trace,
        IPolicyEvaluator policies,
        IResourcePermissionService permissions,
        flow_weaver_backend.Services.Settings.IAppSettingsService appSettings,
        ILogger<WorkflowService> logger)
    {
        _workflows = workflows;
        _caller = caller;
        _schemaValidator = schemaValidator;
        _referenceValidator = referenceValidator;
        _vendorCommandValidator = vendorCommandValidator;
        _audit = audit;
        _trace = trace;
        _policies = policies;
        _permissions = permissions;
        _appSettings = appSettings;
        _logger = logger;
    }

    // Optional gate: when an admin has flipped
    // permissions_granular_gating_enabled in /admin/settings, the
    // service consults IResourcePermissionService for the required role
    // before mutating a workflow. Default off keeps an existing install
    // working without per-resource grants — admins opt in via the UI once
    // they have populated the resource_permissions table.
    private async Task<bool> AuthorizeAsync(Guid workflowId, string requiredRole, CancellationToken ct)
    {
        var settings = await _appSettings.GetAsync(ct);
        if (!settings.PermissionsGranularGatingEnabled) return true;
        return await _permissions.HasAtLeastAsync(ResourceTypes.Workflow, workflowId, requiredRole, ct);
    }

    public async Task<ActionResult<ListResponse<WorkflowResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _workflows.CountAsync();
        var workflows = await _workflows.ListAsync(limit, offset);

        _logger.LogDebug(
            "workflow.list.ok total={Total} returned={Returned}",
            total, workflows.Count);

        return new OkObjectResult(new ListResponse<WorkflowResponse>
        {
            Data = workflows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<WorkflowResponse>> GetByIdAsync(Guid id)
    {
        var wf = await _workflows.GetByIdAsync(id);
        if (wf is null)
        {
            _logger.LogWarning("workflow.get.not_found workflow_id={WorkflowId}", id);
            return new NotFoundObjectResult(new { error = "workflow not found" });
        }

        return ToResponse(wf);
    }

    public async Task<ActionResult<WorkflowResponse>> PostAsync(CreateWorkflow dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("workflow.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }

        var nodes = dto.Nodes is { } n && n.ValueKind != System.Text.Json.JsonValueKind.Undefined ? n : System.Text.Json.JsonDocument.Parse("[]").RootElement;
        var edges = dto.Edges is { } e && e.ValueKind != System.Text.Json.JsonValueKind.Undefined ? e : System.Text.Json.JsonDocument.Parse("[]").RootElement;

        // Auto-layout pass — agent/API callers rarely set x/y or edge
        // handles, so we pre-arrange the DAG before persistence. User-
        // tuned workflows (handles set, positions varied) are detected
        // by NeedsLayout and left untouched.
        if (WorkflowAutoLayout.NeedsLayout(nodes, edges))
            (nodes, edges) = WorkflowAutoLayout.Apply(nodes, edges);

        var validation = _schemaValidator.Validate(nodes, edges);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "workflow.create.validation_failed reason=schema_invalid schema_version={SchemaVersion} errors={ErrorCount}",
                _schemaValidator.CurrentSchemaVersion, validation.Errors.Count);
            return new BadRequestObjectResult(new
            {
                error = "schema_invalid",
                schema_version = _schemaValidator.CurrentSchemaVersion,
                details = validation.Errors,
                // Beside `details`, never instead of it: the message array is what every
                // existing client reads. This is the same failures with the rule and the
                // document location kept, so an editor can mark the offending node instead
                // of parsing prose. Absent when the validator produced none.
                violations = validation.Violations,
            });
        }

        // References pass — integration_action nodes must carry real
        // integration_id / action_id GUIDs pointing to existing rows.
        // Catches agent hallucinations (placeholder strings, deleted
        // row) before we persist an un-runnable workflow that fails
        // opaquely at execution time.
        //
        // Context-aware variant also surfaces warnings (not blocking)
        // like "description says email but no integration_action node"
        // so the agent can self-correct before the user runs it.
        var referenceResult = await _referenceValidator.ValidateWithContextAsync(nodes, dto.Name, dto.Description, default);
        if (!referenceResult.IsValid)
        {
            _logger.LogWarning(
                "workflow.create.validation_failed reason=references_invalid errors={ErrorCount}",
                referenceResult.Errors.Count);
            return new BadRequestObjectResult(new
            {
                error = "references_invalid",
                details = referenceResult.Errors,
            });
        }

        // Vendor-command catalog check. Flags ssh commands that don't
        // match any catalog entry for the resolved device_type. Warnings
        // only — never blocks because the catalog is intentionally
        // non-exhaustive (vendor extensions, custom CLIs). Workflows
        // don't carry target devices at create time; resolution falls
        // back to config_overrides.device_type.
        var vendorResult = await _vendorCommandValidator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        // Corporate guardrails. Evaluate before persisting so a
        // deny-rule stops the write at the API boundary instead of
        // leaving a half-committed workflow behind.
        var policyCtx = new PolicyEvaluationContext(
            Action: "create",
            Environment: DefaultEnvironment,
            WorkflowName: dto.Name,
            WorkflowDescription: dto.Description,
            Nodes: nodes,
            DeviceRoles: Array.Empty<string>(),
            DevicePoolNames: Array.Empty<string>());
        var decision = await _policies.EvaluateAsync(policyCtx, default);
        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "workflow.create.policy_blocked policy={Policy} reason={Reason}",
                decision.PolicyName, decision.Reason);
            await _trace.EventAsync("policy.blocked", "policy", "completed",
                metadata: new { policy = decision.PolicyName, action = "create", reason = decision.Reason });
            return new ObjectResult(new
            {
                error = "policy_blocked",
                policy = decision.PolicyName,
                reason = decision.Reason,
            })
            { StatusCode = 403 };
        }

        var now = DateTime.UtcNow;
        var wf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            Version = 1,
            SchemaVersion = _schemaValidator.CurrentSchemaVersion,
            InputSchema = dto.InputSchema is { } ins && ins.ValueKind != System.Text.Json.JsonValueKind.Undefined ? ins : System.Text.Json.JsonDocument.Parse("{}").RootElement,
            Nodes = nodes,
            Edges = edges,
            Metadata = dto.Metadata is { } m && m.ValueKind != System.Text.Json.JsonValueKind.Undefined ? m : System.Text.Json.JsonDocument.Parse("{}").RootElement,
            Environment = DefaultEnvironment,
            ChangeSummary = string.Empty,
            ConversationId = dto.ConversationId,
            // Same attribution the import pipeline stamps. Agent-created
            // workflows run inside the chat request scope, so this is the
            // chatting user — before this, every workflow created through
            // this path (UI "new workflow" AND agent tools) had
            // created_by=null.
            CreatedBy = _caller.Username,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _workflows.Add(wf);
        try
        {
            await _workflows.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow.create.failed workflow_name={WorkflowName}", wf.Name);
            throw;
        }

        await _audit.LogAsync("workflow", wf.WorkflowId, "create",
            after: new { wf.Name, wf.Environment, wf.Version });
        await _trace.EventAsync("workflow.create", "workflow", "completed",
            metadata: new { workflow_id = wf.WorkflowId, wf.Name, wf.Environment });

        var mergedWarnings = MergeWarnings(referenceResult.Warnings, vendorResult.Warnings);

        _logger.LogInformation(
            "workflow.create.ok workflow_id={WorkflowId} workflow_name={WorkflowName} warnings={WarningCount}",
            wf.WorkflowId, wf.Name, mergedWarnings?.Count ?? 0);

        var response = ToResponse(wf);
        if (mergedWarnings is { Count: > 0 })
            response.Warnings = mergedWarnings;

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Workflow",
            routeValues: new { id = wf.WorkflowId },
            value: response);
    }

    public async Task<ActionResult<WorkflowResponse>> UpdateAsync(Guid id, UpdateWorkflow dto)
    {
        var wf = await _workflows.GetByIdAsync(id);
        if (wf is null)
        {
            _logger.LogWarning("workflow.update.not_found workflow_id={WorkflowId}", id);
            return new NotFoundObjectResult(new { error = "workflow not found" });
        }

        if (!await AuthorizeAsync(id, ResourceRoles.Editor, CancellationToken.None))
        {
            _logger.LogWarning(
                "workflow.update.forbidden workflow_id={WorkflowId} user_id={UserId} reason=missing_editor_grant",
                id, _caller.UserId);
            return new ObjectResult(new { error = "missing_editor_grant" }) { StatusCode = 403 };
        }

        if (wf.Environment == "production")
        {
            _logger.LogWarning(
                "workflow.update.conflict workflow_id={WorkflowId} reason=production_immutable",
                wf.WorkflowId);
            return new ObjectResult(new { error = "production_immutable" }) { StatusCode = 409 };
        }

        // Snapshot BEFORE the dto is applied — `wf` is tracked, so reading
        // these fields after the assignments below would report the new
        // values as the old ones. Captured as a value tuple rather than a
        // reference to the entity for the same reason.
        var auditBefore = new
        {
            wf.Name,
            wf.Description,
            wf.Environment,
            wf.Version,
        };

        var structureChanged = false;
        var pendingNodes = wf.Nodes;
        var pendingEdges = wf.Edges;
        IReadOnlyList<string>? pendingWarnings = null;

        if (dto.Name is not null) wf.Name = dto.Name;
        if (dto.Description is not null) wf.Description = dto.Description;
        if (dto.InputSchema is not null) { wf.InputSchema = dto.InputSchema.Value; structureChanged = true; }
        if (dto.Nodes is not null) { pendingNodes = dto.Nodes.Value; structureChanged = true; }
        if (dto.Edges is not null) { pendingEdges = dto.Edges.Value; structureChanged = true; }
        if (dto.Metadata is not null) wf.Metadata = dto.Metadata.Value;
        if (dto.ChangeSummary is not null) wf.ChangeSummary = dto.ChangeSummary;
        if (dto.ConversationId is not null) wf.ConversationId = dto.ConversationId;

        // Only revalidate when the shape actually changes — avoids paying
        // the schema evaluation cost on cosmetic renames.
        if (dto.Nodes is not null || dto.Edges is not null)
        {
            // Same auto-layout guard as create: only kicks in when the
            // payload signals "machine-authored" (edges without handles
            // or every node at 0/0). User-arranged updates pass through
            // untouched because their handles and coordinates are set.
            if (WorkflowAutoLayout.NeedsLayout(pendingNodes, pendingEdges))
                (pendingNodes, pendingEdges) = WorkflowAutoLayout.Apply(pendingNodes, pendingEdges);

            var validation = _schemaValidator.Validate(pendingNodes, pendingEdges);
            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "workflow.update.validation_failed workflow_id={WorkflowId} reason=schema_invalid errors={ErrorCount}",
                    wf.WorkflowId, validation.Errors.Count);
                return new BadRequestObjectResult(new
                {
                    error = "schema_invalid",
                    schema_version = _schemaValidator.CurrentSchemaVersion,
                    details = validation.Errors,
                    violations = validation.Violations,
                });
            }

            // wf.Nodes is what is already saved: the permission gates apply to what
            // this edit adds, so touching one node doesn't re-ask for the
            // permissions another node needed.
            var referenceResult = await _referenceValidator.ValidateWithContextAsync(pendingNodes,
                dto.Name ?? wf.Name,
                dto.Description ?? wf.Description,
                default,
                previousNodes: wf.Nodes);
            if (!referenceResult.IsValid)
            {
                _logger.LogWarning(
                    "workflow.update.validation_failed workflow_id={WorkflowId} reason=references_invalid errors={ErrorCount}",
                    wf.WorkflowId, referenceResult.Errors.Count);
                return new BadRequestObjectResult(new
                {
                    error = "references_invalid",
                    details = referenceResult.Errors,
                });
            }

            // Vendor-command catalog check (warnings only — see PostAsync).
            var vendorResult = await _vendorCommandValidator.ValidateAsync(pendingNodes, Array.Empty<Guid>(), default);

            // Stash merged warnings for the response builder at the end.
            pendingWarnings = MergeWarnings(referenceResult.Warnings, vendorResult.Warnings);

            wf.Nodes = pendingNodes;
            wf.Edges = pendingEdges;
            wf.SchemaVersion = _schemaValidator.CurrentSchemaVersion;
        }

        if (structureChanged)
        {
            wf.Version += 1;
            // S16 — Harness gate: any structural edit invalidates the
            // previous simulation result, so PromotionService.draft→qa
            // and mark_workflow_ready refuse the workflow until it gets
            // re-simulated. The SimulationResult row itself stays for
            // audit; only the pointer is cleared.
            wf.LastSimulationId = null;
        }

        // Re-evaluate policies on structural updates. A rule
        // targeting `snippet_type: ssh` would otherwise allow someone to
        // edit a non-ssh workflow into an ssh one and bypass the block.
        if (structureChanged)
        {
            var policyCtx = new PolicyEvaluationContext(
                Action: "update",
                Environment: wf.Environment,
                WorkflowName: wf.Name,
                WorkflowDescription: wf.Description,
                Nodes: wf.Nodes,
                DeviceRoles: Array.Empty<string>(),
                DevicePoolNames: Array.Empty<string>());
            var decision = await _policies.EvaluateAsync(policyCtx, default);
            if (!decision.Allowed)
            {
                _logger.LogWarning(
                    "workflow.update.policy_blocked workflow_id={WorkflowId} policy={Policy} reason={Reason}",
                    wf.WorkflowId, decision.PolicyName, decision.Reason);
                await _trace.EventAsync("policy.blocked", "policy", "completed",
                    metadata: new { policy = decision.PolicyName, action = "update", reason = decision.Reason });
                return new ObjectResult(new
                {
                    error = "policy_blocked",
                    policy = decision.PolicyName,
                    reason = decision.Reason,
                })
                { StatusCode = 403 };
            }
        }

        wf.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _workflows.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow.update.failed workflow_id={WorkflowId}", wf.WorkflowId);
            throw;
        }

        // `structure_changed` alone said an edit happened without saying what
        // it was, and the DAG is the part that matters. The node/edge JSON is
        // deliberately NOT copied in — it can be megabytes — but the version
        // bump points at the WorkflowVersion row that holds it.
        await _audit.LogAsync("workflow", wf.WorkflowId, "update",
            before: auditBefore,
            after: new
            {
                wf.Name,
                wf.Description,
                wf.Environment,
                wf.Version,
                structure_changed = structureChanged,
            });
        await _trace.EventAsync("workflow.update", "workflow", "completed",
            metadata: new { workflow_id = wf.WorkflowId, wf.Version, structure_changed = structureChanged });

        _logger.LogInformation(
            "workflow.update.ok workflow_id={WorkflowId} version={Version} structure_changed={StructureChanged} warnings={WarningCount}",
            wf.WorkflowId, wf.Version, structureChanged, pendingWarnings?.Count ?? 0);

        var response = ToResponse(wf);
        if (pendingWarnings is { Count: > 0 })
            response.Warnings = pendingWarnings;
        return response;
    }

    public async Task<ActionResult<WorkflowResponse>> DeleteAsync(Guid id)
    {
        var wf = await _workflows.GetByIdAsync(id);
        if (wf is null)
        {
            _logger.LogWarning("workflow.delete.not_found workflow_id={WorkflowId}", id);
            return new NotFoundObjectResult(new { error = "workflow not found" });
        }

        if (!await AuthorizeAsync(id, ResourceRoles.Owner, CancellationToken.None))
        {
            _logger.LogWarning(
                "workflow.delete.forbidden workflow_id={WorkflowId} user_id={UserId} reason=missing_owner_grant",
                id, _caller.UserId);
            return new ObjectResult(new { error = "missing_owner_grant" }) { StatusCode = 403 };
        }


        if (wf.Environment == "production")
        {
            _logger.LogWarning(
                "workflow.delete.conflict workflow_id={WorkflowId} reason=production_immutable",
                wf.WorkflowId);
            return new ObjectResult(new { error = "production_immutable" }) { StatusCode = 409 };
        }

        wf.IsActive = false;
        wf.UpdatedAt = DateTime.UtcNow;
        await _workflows.SaveChangesAsync();

        await _audit.LogAsync("workflow", wf.WorkflowId, "delete",
            before: new { wf.Name, wf.Environment, wf.Version });
        await _trace.EventAsync("workflow.delete", "workflow", "completed",
            metadata: new { workflow_id = wf.WorkflowId, wf.Name });

        _logger.LogInformation("workflow.delete.ok workflow_id={WorkflowId}", wf.WorkflowId);
        return ToResponse(wf);
    }

    // Concatenate the warnings from each validator pass. Either side may
    // be null/empty; we collapse to null so the response setter only
    // attaches `Warnings` when there's something to report.
    private static IReadOnlyList<string>? MergeWarnings(
        IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        var aCount = a?.Count ?? 0;
        var bCount = b?.Count ?? 0;
        if (aCount == 0 && bCount == 0) return null;
        if (bCount == 0) return a;
        if (aCount == 0) return b;
        var merged = new List<string>(aCount + bCount);
        merged.AddRange(a!);
        merged.AddRange(b!);
        return merged;
    }

    private static System.Text.Json.JsonElement SafeJson(System.Text.Json.JsonElement el, string fallback = "null") =>
        el.ValueKind == System.Text.Json.JsonValueKind.Undefined
            ? System.Text.Json.JsonDocument.Parse(fallback).RootElement
            : el;

    private static WorkflowResponse ToResponse(WorkflowModel w) => new()
    {
        WorkflowId = w.WorkflowId,
        Name = w.Name,
        Description = w.Description,
        Version = w.Version,
        SchemaVersion = w.SchemaVersion,
        InputSchema = SafeJson(w.InputSchema, "{}"),
        Nodes = SafeJson(w.Nodes, "[]"),
        Edges = SafeJson(w.Edges, "[]"),
        Metadata = SafeJson(w.Metadata, "{}"),
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
