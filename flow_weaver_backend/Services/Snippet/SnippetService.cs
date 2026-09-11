using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Services.Snippet;

public class SnippetService : ISnippet
{
    private const int DefaultMaxParallel = 1;
    private const int DefaultTimeoutSeconds = 300;

    private readonly ISnippetRepository _snippets;
    // Read-only: used to tell a proven snippet from a draft (see ToResponse).
    // A snippet never writes step runs, so this dependency is one-directional.
    private readonly IStepRunRepository _stepRuns;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly ILogger<SnippetService> _logger;

    public SnippetService(
        ISnippetRepository snippets,
        IStepRunRepository stepRuns,
        ICurrentUser caller,
        IAuditLogger audit,
        ITraceLogger trace,
        ILogger<SnippetService> logger)
    {
        _snippets = snippets;
        _stepRuns = stepRuns;
        _caller = caller;
        _audit = audit;
        _trace = trace;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<SnippetResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _snippets.CountAsync();
        var defs = await _snippets.ListAsync(limit, offset);

        // One grouped query for the whole page, not one per row. The palette
        // uses these counts to file never-completed snippets under "Unproven"
        // instead of padding the reusable list with drafts.
        var stats = await _stepRuns.GetCompletedStatsBySnippetAsync(
            defs.Select(d => d.SnippetId).ToList());

        _logger.LogDebug(
            "snippet.list.ok total={Total} returned={Returned} proven={Proven}",
            total, defs.Count, stats.Count);

        return new OkObjectResult(new ListResponse<SnippetResponse>
        {
            Data = defs.Select(d => ToResponse(d, stats.GetValueOrDefault(d.SnippetId))).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<SnippetResponse>> GetByIdAsync(Guid id)
    {
        var def = await _snippets.GetByIdAsync(id);
        if (def is null)
        {
            _logger.LogWarning("snippet.get.not_found snippet_id={SnippetId}", id);
            return new NotFoundObjectResult(new { error = "snippet not found" });
        }

        var stats = await _stepRuns.GetCompletedStatsBySnippetAsync(new[] { def.SnippetId });
        return ToResponse(def, stats.GetValueOrDefault(def.SnippetId));
    }

    public async Task<ActionResult<SnippetResponse>> PostAsync(CreateSnippet dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("snippet.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Type))
        {
            _logger.LogWarning("snippet.create.validation_failed reason=type_required");
            return new BadRequestObjectResult(new { error = "type is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.TargetMode))
        {
            _logger.LogWarning("snippet.create.validation_failed reason=target_mode_required");
            return new BadRequestObjectResult(new { error = "target_mode is required" });
        }

        var scaffoldError = DetectScaffoldCode(dto.Type, dto.Code);
        if (scaffoldError is not null)
        {
            _logger.LogWarning(
                "snippet.create.validation_failed reason=scaffold_code snippet_type={Type}",
                dto.Type);
            return new BadRequestObjectResult(new { error = scaffoldError });
        }

        var mermaidError = ValidateLogicDiagram(dto.Type, dto.LogicDiagramMermaid);
        if (mermaidError is not null)
        {
            _logger.LogWarning(
                "snippet.create.validation_failed reason=logic_diagram_invalid snippet_type={Type}",
                dto.Type);
            return new BadRequestObjectResult(new { error = mermaidError });
        }

        // network_enabled lifts the sandbox network isolation — only admins may
        // set it, and only on python_snippet.
        var netGuard = GuardNetworkEnabled(dto.NetworkEnabled ?? false, dto.Type);
        if (netGuard is not null)
        {
            _logger.LogWarning(
                "snippet.create.validation_failed reason=network_enabled_forbidden snippet_type={Type}", dto.Type);
            return netGuard;
        }

        var now = DateTime.UtcNow;
        var def = new SnippetModel
        {
            SnippetId = Guid.NewGuid(),
            Name = dto.Name,
            // Cross-instance identity, fixed at creation. A workflow bundle
            // shared with another instance names this snippet by it.
            Slug = Common.Slug.Unique(dto.Name, await _snippets.ListTakenSlugsAsync()),
            Type = dto.Type,
            Description = dto.Description,
            InputSchema = dto.InputSchema ?? default,
            OutputSchema = dto.OutputSchema ?? default,
            Code = dto.Code,
            ScriptLanguage = dto.ScriptLanguage,
            TargetMode = dto.TargetMode,
            MaxParallel = dto.MaxParallel ?? DefaultMaxParallel,
            TimeoutSeconds = dto.TimeoutSeconds ?? DefaultTimeoutSeconds,
            Verified = dto.Verified ?? false,
            RetryPolicy = dto.RetryPolicy ?? default,
            LogicDiagramMermaid = dto.LogicDiagramMermaid,
            Idempotency = NormalizeIdempotency(dto.Idempotency),
            ChangesState = dto.ChangesState,
            NetworkEnabled = dto.NetworkEnabled ?? false,
            // Same attribution the import pipeline stamps. Covers the UI and
            // the agent's create_snippet tool (both route through here) —
            // before this, every snippet they created had created_by=null.
            CreatedBy = _caller.Username,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _snippets.Add(def);
        try
        {
            await _snippets.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "snippet.create.failed snippet_name={SnippetName}", def.Name);
            throw;
        }

        await _audit.LogAsync("snippet", def.SnippetId, "create",
            after: new { def.Name, def.Type, def.TargetMode });
        await _trace.EventAsync("snippet.create", "workflow", "completed",
            metadata: new { snippet_id = def.SnippetId, def.Name, def.Type });

        _logger.LogInformation(
            "snippet.create.ok snippet_id={SnippetId} snippet_name={SnippetName}",
            def.SnippetId, def.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Snippet",
            routeValues: new { id = def.SnippetId },
            value: ToResponse(def));
    }

    public async Task<ActionResult<SnippetResponse>> UpdateAsync(Guid id, UpdateSnippet dto)
    {
        var def = await _snippets.GetByIdAsync(id);
        if (def is null)
        {
            _logger.LogWarning("snippet.update.not_found snippet_id={SnippetId}", id);
            return new NotFoundObjectResult(new { error = "snippet not found" });
        }

        // A network-enabled snippet is privileged (runs with the sandbox network
        // isolation lifted), so only an admin may edit it at all — including its
        // code. Toggling the flag itself is guarded again below.
        if (def.NetworkEnabled && !IsAdmin())
        {
            _logger.LogWarning(
                "snippet.update.forbidden snippet_id={SnippetId} reason=network_enabled_admin_only", def.SnippetId);
            return Forbidden("editing a network-enabled snippet requires the admin role");
        }

        // Validate the POST-MERGE shape before touching the entity. `def` is
        // tracked, so mutating first and rejecting afterwards would leave the
        // invalid code/diagram in the change tracker, ready to be written by
        // the next SaveChanges in the same unit of work.
        var mergedType = dto.Type ?? def.Type;
        var mergedCode = dto.Code ?? def.Code;
        var mergedDiagram = dto.LogicDiagramMermaid ?? def.LogicDiagramMermaid;

        var pendingScaffoldError = DetectScaffoldCode(mergedType, mergedCode);
        if (pendingScaffoldError is not null)
        {
            _logger.LogWarning(
                "snippet.update.validation_failed snippet_id={SnippetId} reason=scaffold_code",
                def.SnippetId);
            return new BadRequestObjectResult(new { error = pendingScaffoldError });
        }

        var pendingMermaidError = ValidateLogicDiagram(mergedType, mergedDiagram);
        if (pendingMermaidError is not null)
        {
            _logger.LogWarning(
                "snippet.update.validation_failed snippet_id={SnippetId} reason=logic_diagram_invalid",
                def.SnippetId);
            return new BadRequestObjectResult(new { error = pendingMermaidError });
        }

        // Pre-mutation snapshot for the audit (`def` is tracked). Code is not
        // copied in — it can be large and lands in the snippet's own history —
        // but `code_changed` records that the executed body was rewritten,
        // which for a python_snippet is the security-relevant fact.
        var auditBefore = new
        {
            def.Name,
            def.Type,
            def.TargetMode,
            def.Verified,
            def.NetworkEnabled,
        };

        if (dto.Name is not null) def.Name = dto.Name;
        if (dto.Type is not null) def.Type = dto.Type;
        if (dto.Description is not null) def.Description = dto.Description;
        if (dto.InputSchema is not null) def.InputSchema = dto.InputSchema.Value;
        if (dto.OutputSchema is not null) def.OutputSchema = dto.OutputSchema.Value;
        if (dto.Code is not null) def.Code = dto.Code;
        if (dto.ScriptLanguage is not null) def.ScriptLanguage = dto.ScriptLanguage;
        if (dto.TargetMode is not null) def.TargetMode = dto.TargetMode;
        if (dto.LogicDiagramMermaid is not null) def.LogicDiagramMermaid = dto.LogicDiagramMermaid;
        // Idempotency uses PATCH semantics: omit (default-bind null) to
        // keep the current value. We can't tell "client sent null" from
        // "client omitted" without a sentinel; today the only way to
        // clear the override is to send a non-null sentinel value of
        // "" which we map back to null. Acceptable until we adopt
        // System.Text.Json's JsonElement-based PATCH model.
        if (dto.Idempotency is not null)
            def.Idempotency = NormalizeIdempotency(dto.Idempotency);
        if (dto.ChangesState is not null)
            def.ChangesState = dto.ChangesState.Value;

        // Scaffold detection and the Mermaid check already ran on the merged
        // values above, before any mutation.

        if (dto.NetworkEnabled is not null)
        {
            var netGuard = GuardNetworkEnabled(dto.NetworkEnabled.Value, def.Type);
            if (netGuard is not null) return netGuard;
            def.NetworkEnabled = dto.NetworkEnabled.Value;
        }

        if (dto.MaxParallel is not null) def.MaxParallel = dto.MaxParallel.Value;
        if (dto.TimeoutSeconds is not null) def.TimeoutSeconds = dto.TimeoutSeconds.Value;
        if (dto.Verified is not null) def.Verified = dto.Verified.Value;
        if (dto.RetryPolicy is not null) def.RetryPolicy = dto.RetryPolicy.Value;

        def.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _snippets.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "snippet.update.failed snippet_id={SnippetId}", def.SnippetId);
            throw;
        }

        await _audit.LogAsync("snippet", def.SnippetId, "update",
            before: auditBefore,
            after: new
            {
                def.Name,
                def.Type,
                def.TargetMode,
                def.Verified,
                def.NetworkEnabled,
                code_changed = dto.Code is not null,
            });
        await _trace.EventAsync("snippet.update", "workflow", "completed",
            metadata: new { snippet_id = def.SnippetId, def.Name });

        _logger.LogInformation("snippet.update.ok snippet_id={SnippetId}", def.SnippetId);
        return ToResponse(def);
    }

    public async Task<ActionResult<SnippetResponse>> DeleteAsync(Guid id)
    {
        var def = await _snippets.GetByIdAsync(id);
        if (def is null)
        {
            _logger.LogWarning("snippet.delete.not_found snippet_id={SnippetId}", id);
            return new NotFoundObjectResult(new { error = "snippet not found" });
        }

        def.IsActive = false;
        def.UpdatedAt = DateTime.UtcNow;
        await _snippets.SaveChangesAsync();

        await _audit.LogAsync("snippet", def.SnippetId, "delete",
            before: new { def.Name, def.Type });
        await _trace.EventAsync("snippet.delete", "workflow", "completed",
            metadata: new { snippet_id = def.SnippetId, def.Name });

        _logger.LogInformation("snippet.delete.ok snippet_id={SnippetId}", def.SnippetId);
        return ToResponse(def);
    }

    // Guards against the "agent generates scaffold" anti-pattern we
    // observed in the LLDP audit workflow: the AI, instead of writing
    // the actual NetBox/SSH logic, produced Python snippets whose body
    // was something like:
    //
    //   set_output({"status": "placeholder",
    //               "message": "Scaffold snippet created for X sync workflow"})
    //
    // Those snippets pass `worker.python.ok` with exit_code=0, downstream
    // steps render the placeholder text into reports, and the workflow
    // looks "green" while doing nothing. We reject the snippet at
    // create/update time so the anti-pattern can't land in the DB.
    //
    // Detection is deliberately conservative — we only flag snippets
    // that have BOTH a placeholder marker AND no call into real work
    // (no `integration(`, no external I/O). A legit script discussing
    // placeholders in comments is fine.
    //
    // Only applies to script-carrying snippet types (python_snippet,
    // transform). Integration_action / ssh / ping / report don't store
    // user-authored Code so their path skips this check entirely.
    private static readonly HashSet<string> ScriptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "python_snippet", "python", "transform", "jmespath", "ansible_playbook",
    };

    private static readonly string[] ScaffoldMarkers =
    {
        "scaffold snippet",
        "placeholder snippet",
        "todo: implement",
        "fixme: implement",
        "not yet implemented",
        "replace this placeholder",
    };

    private static string? DetectScaffoldCode(string? snippetType, string? code)
    {
        if (string.IsNullOrWhiteSpace(snippetType)) return null;
        if (!ScriptTypes.Contains(snippetType)) return null;
        if (string.IsNullOrWhiteSpace(code)) return null;

        var hit = ScaffoldMarkers.FirstOrDefault(m =>
            code.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return null;

        // Second gate: if the script actually does real work (calls
        // integration(), spawns a subprocess, opens a socket) we allow
        // it even with the marker — it's probably a legit script that
        // mentions "placeholder" in a log message.
        var doesRealWork = code.Contains("integration(", StringComparison.Ordinal)
            || code.Contains("subprocess", StringComparison.OrdinalIgnoreCase)
            || code.Contains("socket.", StringComparison.OrdinalIgnoreCase);
        if (doesRealWork) return null;

        return $"snippet code looks like a scaffold (contains '{hit}') and makes no "
             + "integration/subprocess/socket call — implement the real logic or use a "
             + "specialized snippet (integration_action for API calls, ssh for CLI, "
             + "report for artifacts). If this is a legitimate script that mentions "
             + "placeholders in logs/comments, add a real integration(...) call so the "
             + "detector knows it's wired to something.";
    }

    // Types that REQUIRE a logic_diagram_mermaid. These are the snippet
    // flavors where the behavior is entirely in user-authored code and
    // therefore opaque without the diagram. Built-in types (ping,
    // rest_call, ssh, integration_action, report, snmp_v3, netconf,
    // ansible_playbook) carry their semantics in the handler + Type
    // itself, so a diagram is optional there.
    private static readonly HashSet<string> MermaidRequiredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "python_snippet", "python", "transform", "jmespath",
    };

    // First-non-blank-line keywords that mean "this is likely valid
    // Mermaid". We're deliberately shallow — full Mermaid validation
    // lives in the renderer (the UI catches typos visually). The goal
    // is to reject obvious mistakes (agent pasted Python, empty string,
    // random prose) before they reach the DB.
    private static readonly string[] MermaidDirectives =
    {
        "graph", "flowchart", "sequenceDiagram", "stateDiagram", "stateDiagram-v2",
        "classDiagram", "erDiagram", "gantt", "journey", "gitGraph",
        "pie", "mindmap", "timeline", "quadrantChart", "requirementDiagram",
    };

    // Returns null when the mermaid is acceptable, an error message when
    // it must be rejected. Rules:
    //  - For MermaidRequiredTypes, the field must be present AND start
    //    with a known Mermaid directive.
    //  - For other types, any non-null value must still start with a
    //    directive (so a bad paste trips here instead of rendering
    //    garbage on the frontend).
    //  - Null is always acceptable for non-required types.
    private static string? ValidateLogicDiagram(string? snippetType, string? diagram)
    {
        var required = !string.IsNullOrWhiteSpace(snippetType)
                       && MermaidRequiredTypes.Contains(snippetType);

        if (string.IsNullOrWhiteSpace(diagram))
        {
            return required
                ? $"logic_diagram_mermaid is required for type='{snippetType}'. "
                  + "Produce a Mermaid diagram (graph TD / flowchart / sequenceDiagram) "
                  + "that describes the task's inputs, decision branches, and outputs. "
                  + "See Skills/mermaid.md for conventions."
                : null;
        }

        // Find the first non-blank, non-comment line.
        string? firstLine = null;
        foreach (var raw in diagram.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("%%", StringComparison.Ordinal)) continue; // Mermaid comment
            firstLine = line;
            break;
        }

        if (firstLine is null)
        {
            return "logic_diagram_mermaid has no non-blank content";
        }

        var matchesDirective = MermaidDirectives.Any(d =>
            firstLine.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        if (!matchesDirective)
        {
            return $"logic_diagram_mermaid must start with a Mermaid directive "
                 + $"({string.Join(", ", MermaidDirectives.Take(6))}, …). "
                 + $"First non-blank line: '{TruncatePreview(firstLine, 80)}'";
        }

        return null;
    }

    private static string TruncatePreview(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // Maps the wire string ("idempotent" / "requires_compensation" /
    // "non_reversible") to the canonical lowercase form, or null when
    // the caller passed null/blank/sentinel. Unknown values are rejected
    // by the BD CHECK constraint at SaveChanges time — we don't reject
    // here so test fixtures can round-trip arbitrary strings via
    // EF InMemory (which does not enforce CHECKs).
    private static string? NormalizeIdempotency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant();
    }

    // python_snippet flavors that may carry network-enabled code.
    private static readonly HashSet<string> PythonTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "python_snippet", "python",
    };

    private bool IsAdmin() =>
        _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

    private static ObjectResult Forbidden(string error) =>
        new(new { error }) { StatusCode = StatusCodes.Status403Forbidden };

    // Validates an attempt to set network_enabled. Returns an error result when
    // it must be rejected, or null when allowed. network_enabled lifts the
    // sandbox's network isolation, so it's admin-only and python_snippet-only.
    private ActionResult? GuardNetworkEnabled(bool wantNetwork, string? type)
    {
        if (!wantNetwork) return null;
        if (!PythonTypes.Contains(type ?? string.Empty))
            return new BadRequestObjectResult(new { error = "network_enabled only applies to python_snippet" });
        if (!IsAdmin())
            return Forbidden(
                "network_enabled requires the admin role — it runs the snippet with the sandbox "
                + "network isolation lifted (netmiko/paramiko + host network).");
        return null;
    }

    // `stats` is null wherever the caller has no reason to have loaded them
    // (create, update, delete) — the response then carries 0 / null, which is
    // the honest answer for a snippet that has not run in this request.
    private static SnippetResponse ToResponse(SnippetModel s, SnippetRunStats? stats = null) => new()
    {
        CompletedRunCount = stats?.CompletedRuns ?? 0,
        LastCompletedRunAt = stats?.LastCompletedAt,
        SnippetId = s.SnippetId,
        Name = s.Name,
        Type = s.Type,
        Description = s.Description,
        InputSchema = s.InputSchema,
        OutputSchema = s.OutputSchema,
        Code = s.Code,
        ScriptLanguage = s.ScriptLanguage,
        TargetMode = s.TargetMode,
        MaxParallel = s.MaxParallel,
        TimeoutSeconds = s.TimeoutSeconds,
        Verified = s.Verified,
        RetryPolicy = s.RetryPolicy,
        LogicDiagramMermaid = s.LogicDiagramMermaid,
        Idempotency = s.Idempotency,
        ChangesState = s.ChangesState,
        NetworkEnabled = s.NetworkEnabled,
        CreatedBy = s.CreatedBy,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
