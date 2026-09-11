using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// FR-023: CRUD for corporate guardrails. Read is open to any viewer so
// operators can see why a create/run got blocked; mutations require
// Admin because a bad rule can freeze everyone's ability to edit
// workflows.
[ApiController]
[Route("api/[controller]")]
[HasPermission("policy.read")]
public class PolicyController : ControllerBase
{
    private readonly IPolicy _service;
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;

    public PolicyController(IPolicy service, AppDbContext db, ICurrentUser caller)
    {
        _service = service;
        _db = db;
        _caller = caller;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<PolicyResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<PolicyResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("policy.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PolicyResponse>> Post([FromBody] CreatePolicy dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("policy.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PolicyResponse>> Update(Guid id, [FromBody] UpdatePolicy dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("policy.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PolicyResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    public sealed class PolicyAuditEntry
    {
        public DateTime at { get; set; }
        public string? policy_name { get; set; }
        public string? action { get; set; }
        public string? reason { get; set; }
        public string? request_id { get; set; }
        public Guid? user_id { get; set; }
    }

    public sealed class PolicyAuditResponse
    {
        public int total { get; set; }
        public Dictionary<string, int> by_policy { get; set; } = new();
        public List<PolicyAuditEntry> recent { get; set; } = new();
    }

    // FR-023 audit view. Reads `policy.blocked` trace events so admins
    // can see which rules actually fire, how often, and who tripped
    // them. Kept as a lightweight read instead of a full analytics
    // pipeline — the trace_events table is already indexed on
    // (At DESC) so a 7-day window over the blocked action scans at
    // most a few hundred rows in practice.
    [HttpGet("audit")]
    [HasPermission("policy.read")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<PolicyAuditResponse>> Audit(
        [FromQuery] int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var rows = await _db.TraceEvents
            .AsNoTracking()
            .Where(e => e.Action == "policy.blocked"
                && e.At >= cutoff)
            .OrderByDescending(e => e.At)
            .Take(500)
            .Select(e => new { e.At, e.Metadata, e.RequestId, e.UserId })
            .ToListAsync(ct);

        var entries = rows.Select(r =>
        {
            string? policyName = null;
            string? action = null;
            string? reason = null;
            if (r.Metadata.ValueKind == JsonValueKind.Object)
            {
                policyName = GetString(r.Metadata, "policy");
                action = GetString(r.Metadata, "action");
                reason = GetString(r.Metadata, "reason");
            }
            return new PolicyAuditEntry
            {
                at = r.At,
                policy_name = policyName,
                action = action,
                reason = reason,
                request_id = r.RequestId,
                user_id = r.UserId,
            };
        }).ToList();

        var grouped = entries
            .Where(e => !string.IsNullOrEmpty(e.policy_name))
            .GroupBy(e => e.policy_name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(new PolicyAuditResponse
        {
            total = entries.Count,
            by_policy = grouped,
            recent = entries,
        });
    }

    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
