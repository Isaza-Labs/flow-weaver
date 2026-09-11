using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Utils.Report;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Two surfaces on the same controller:
//   - POST /api/reports                          — any authenticated user
//   - GET /api/admin/reports (+ /{id} + download + delete) — admins only
//
// Splitting by auth rather than by route prefix keeps all the report
// wiring in one place, and ASP.NET's per-action [Authorize] resolves
// the right policy on each endpoint.
[ApiController]
[HasPermission("report.read")]
public class ReportsController : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly IReportService _reports;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IAuditLogger _audit;

    public ReportsController(
        AppDbContext db,
        ICurrentUser caller,
        IReportService reports,
        ICredentialEncryptionService crypto,
        IAuditLogger audit)
    {
        _db = db;
        _caller = caller;
        _reports = reports;
        _crypto = crypto;
        _audit = audit;
    }

    // ─── Public-ish: generate + download ────────────────────────────────

    [HttpPost("/api/reports")]
    [EnableRateLimiting(RateLimitingConfiguration.AiChat)]
    public async Task<IActionResult> Generate(
        [FromBody] GenerateReportRequest request,
        [FromHeader(Name = "X-Workflow-Run-Id")] Guid? workflowRunId,
        [FromHeader(Name = "X-Workflow-Id")] Guid? workflowId,
        CancellationToken ct)
    {
        var source = workflowRunId.HasValue ? "workflow" : "api";
        var context = new ReportGenerationContext
        {
            UserId = _caller.IsAuthenticated ? _caller.UserId : null,
            RequestId = HttpContext.TraceIdentifier,
            WorkflowRunId = workflowRunId,
            WorkflowId = workflowId,
        };

        try
        {
            var artifact = await _reports.GenerateAndPersistAsync(request, source, context, ct);
            var bytes = _reports.Decompress(artifact);
            return File(bytes, artifact.ContentType, artifact.Filename);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.BadRequest(ex.Message, code: "report_invalid");
        }
    }

    // ─── Admin: list + detail + download + delete ───────────────────────

    [HttpGet("/api/admin/reports")]
    [HasPermission("report.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<object>> List(
        [FromQuery] Guid? user_id,
        [FromQuery] string? format,
        [FromQuery] string? source,
        [FromQuery] Guid? conversation_id,
        [FromQuery] Guid? workflow_run_id,
        [FromQuery] string? search,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);

        var q = _db.ReportArtifacts.AsNoTracking()
            .Where(r => r.IsActive);

        if (user_id.HasValue) q = q.Where(r => r.UserId == user_id.Value);
        if (!string.IsNullOrWhiteSpace(format)) q = q.Where(r => r.Format == format);
        if (!string.IsNullOrWhiteSpace(source))
        {
            // Comma-separated values are OR-ed — the artifacts view's Export
            // tab asks for `agent,api` (everything not produced by a workflow).
            var sources = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = sources.Length == 1
                ? q.Where(r => r.Source == sources[0])
                : q.Where(r => sources.Contains(r.Source));
        }
        if (conversation_id.HasValue) q = q.Where(r => r.AgentConversationId == conversation_id.Value);
        if (workflow_run_id.HasValue) q = q.Where(r => r.WorkflowRunId == workflow_run_id.Value);
        if (from.HasValue) q = q.Where(r => r.CreatedAt >= from.Value);
        if (to.HasValue) q = q.Where(r => r.CreatedAt <= to.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE is Postgres-native case-insensitive matching and
            // — unlike `Title.ToLower().Contains(...)` — can ride a
            // pg_trgm GIN index on Title for sub-linear lookups.
            var term = search.Trim();
            q = q.Where(r => EF.Functions.ILike(r.Title, $"%{term}%"));
        }

        var total = await q.CountAsync(ct);

        // Left-join Users so username reflects the CURRENT name, not a
        // stale denormalized one. If the user is gone the row still
        // shows up with username = null. Left-join workflow_runs so the UI
        // can tell a schedule-triggered document from a user-triggered one.
        var page = await (
            from r in q.OrderByDescending(r => r.CreatedAt).Skip(offset).Take(clamped)
            join u in _db.Users.AsNoTracking() on r.UserId equals u.UserId into usersLeft
            from u in usersLeft.DefaultIfEmpty()
            join wr in _db.WorkflowRuns.AsNoTracking() on r.WorkflowRunId equals (Guid?)wr.WorkflowRunId into runsLeft
            from wr in runsLeft.DefaultIfEmpty()
            select new ReportArtifactSummary
            {
                ReportArtifactId = r.ReportArtifactId,
                Title = r.Title,
                Filename = r.Filename,
                Format = r.Format,
                ContentType = r.ContentType,
                SizeBytes = r.SizeBytes,
                Source = r.Source,
                UserId = r.UserId,
                Username = u != null ? u.Username : null,
                AgentConversationId = r.AgentConversationId,
                WorkflowRunId = r.WorkflowRunId,
                RunTrigger = wr != null ? wr.Trigger : null,
                AgentPromptRedacted = r.AgentPromptRedacted,
                HasAgentPrompt = r.AgentPromptEncrypted != null,
                ExpiresAt = r.ExpiresAt,
                CreatedAt = r.CreatedAt,
            }
        ).ToListAsync(ct);

        return Ok(new
        {
            data = page,
            total,
            limit = clamped,
            offset,
        });
    }

    [HttpGet("/api/admin/reports/{id:guid}")]
    [HasPermission("report.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<ReportArtifactResponse>> Get(Guid id, CancellationToken ct)
    {
        var row = await _db.ReportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportArtifactId == id
                                      && r.IsActive, ct);
        if (row is null) return NotFound();

        var username = row.UserId.HasValue
            ? await _db.Users.AsNoTracking()
                .Where(u => u.UserId == row.UserId.Value)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(ct)
            : null;

        // For workflow artifacts, expose how the run was triggered so the UI
        // can attribute schedule output to the schedule instead of showing an
        // empty user.
        var runTrigger = row.WorkflowRunId.HasValue
            ? await _db.WorkflowRuns.AsNoTracking()
                .Where(wr => wr.WorkflowRunId == row.WorkflowRunId.Value)
                .Select(wr => wr.Trigger)
                .FirstOrDefaultAsync(ct)
            : null;

        var prompt = row.AgentPromptEncrypted is { Length: > 0 }
            ? _crypto.Decrypt(row.AgentPromptEncrypted)
            : null;

        // Reading the prompt is an auditable action in its own right.
        // We only log it when there IS a prompt — a user with no agent
        // context shouldn't trigger a spurious "read_prompt" row.
        if (prompt is not null)
        {
            await _audit.LogAsync("report", row.ReportArtifactId, "read_prompt",
                after: new { reader_user_id = _caller.UserId, artifact_user_id = row.UserId },
                ct: ct);
        }

        var rawBytes = _reports.Decompress(row);

        return Ok(new ReportArtifactResponse
        {
            ReportArtifactId = row.ReportArtifactId,
            Title = row.Title,
            Filename = row.Filename,
            Format = row.Format,
            ContentType = row.ContentType,
            SizeBytes = row.SizeBytes,
            Sha256 = row.Sha256,
            Base64 = Convert.ToBase64String(rawBytes),
            Source = row.Source,
            UserId = row.UserId,
            Username = username,
            AgentConversationId = row.AgentConversationId,
            AgentRunId = row.AgentRunId,
            AgentName = row.AgentName,
            AgentPrompt = prompt,
            AgentPromptRedacted = row.AgentPromptRedacted,
            WorkflowRunId = row.WorkflowRunId,
            RunTrigger = runTrigger,
            WorkflowId = row.WorkflowId,
            RequestId = row.RequestId,
            ExpiresAt = row.ExpiresAt,
            CreatedAt = row.CreatedAt,
        });
    }

    // Owner-scoped download so non-admin users can pull the reports they
    // generated via chat without gaining access to everyone else's. Uses
    // the same artifact id the tool_result SSE event carries so the chat
    // UI can point directly here for a one-click download.
    [HttpGet("/api/reports/{id:guid}/download")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<IActionResult> DownloadOwn(Guid id, CancellationToken ct)
    {
        var row = await _db.ReportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportArtifactId == id
                                      && r.IsActive, ct);
        if (row is null) return NotFound();

        var isOwner = row.UserId.HasValue && row.UserId == _caller.UserId;
        var isAdmin = User.IsInRole("admin");
        if (!isOwner && !isAdmin) return Forbid();

        var bytes = _reports.Decompress(row);
        return File(bytes, row.ContentType, row.Filename);
    }

    [HttpGet("/api/admin/reports/{id:guid}/download")]
    [HasPermission("report.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var row = await _db.ReportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportArtifactId == id
                                      && r.IsActive, ct);
        if (row is null) return NotFound();

        var bytes = _reports.Decompress(row);

        await _audit.LogAsync("report", row.ReportArtifactId, "download",
            after: new { reader_user_id = _caller.UserId }, ct: ct);

        return File(bytes, row.ContentType, row.Filename);
    }

    [HttpDelete("/api/admin/reports/{id:guid}")]
    [HasPermission("report.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _db.ReportArtifacts
            .FirstOrDefaultAsync(r => r.ReportArtifactId == id
                                      && r.IsActive, ct);
        if (row is null) return NotFound();

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("report", row.ReportArtifactId, "delete",
            before: new { row.Title, row.Format }, ct: ct);

        return NoContent();
    }
}
