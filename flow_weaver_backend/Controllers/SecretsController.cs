using System.Security.Claims;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SecretModel = flow_weaver_backend.Models.Secret;

namespace flow_weaver_backend.Controllers;

// CRUD for named secrets consumed by the AI agent's
// ExecuteOperation tool and the REST spec executor. Admin-only — a
// compromised operator account must not be able to exfiltrate or rotate
// production API keys.
//
// Plaintext values never leave this controller: list/get return only
// metadata and a "has_value" boolean. Admins rotate by writing a new
// value; there is no "reveal" endpoint on purpose.
[ApiController]
[Route("api/secrets")]
[Authorize(Policy = "Admin")]
public class SecretsController : ControllerBase
{
    // Secrets are referenced via ${secret:secret:<name>:value} in spec
    // templates, so the name must fit a URL/host-header-safe grammar:
    // lowercase alphanumerics + hyphens.
    private static readonly Regex NameRegex = new(
        "^[a-z0-9][a-z0-9_-]{0,62}[a-z0-9]$",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;

    public SecretsController(
        AppDbContext db,
        ICurrentUser caller,
        ICredentialEncryptionService crypto,
        IAuditLogger audit,
        ITraceLogger trace)
    {
        _db = db;
        _caller = caller;
        _crypto = crypto;
        _audit = audit;
        _trace = trace;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<SecretResponse>>> List(CancellationToken ct)
    {
        var rows = await _db.Secrets.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        return Ok(rows.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<SecretResponse>> Get(Guid id, CancellationToken ct)
    {
        var row = await _db.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SecretId == id
                                      && s.IsActive, ct);
        return row is null ? NotFound() : Ok(ToResponse(row));
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public async Task<ActionResult<SecretResponse>> Create(
        [FromBody] CreateSecretRequest request, CancellationToken ct)
    {
        var validation = Validate(request.Name, request.Value);
        if (validation is not null) return Problems.BadRequest(validation, code: "secret_invalid");

        var exists = await _db.Secrets
            .AnyAsync(s => s.Name == request.Name, ct);
        if (exists)
            return Problems.Conflict("a secret with that name already exists", code: "secret_name_taken");

        var now = DateTime.UtcNow;
        var row = new SecretModel
        {
            SecretId = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            EncryptedValue = _crypto.Encrypt(request.Value) ?? Array.Empty<byte>(),
            CreatedBy = User.FindFirstValue(ClaimTypes.NameIdentifier),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Secrets.Add(row);
        await _db.SaveChangesAsync(ct);

        // Never put the value in the audit payload — the row's existence
        // is enough for "who created this" traceability.
        await _audit.LogAsync("secret", row.SecretId, "create",
            null, new { row.Name, row.Description }, ct);
        await _trace.EventAsync("secret.create", "admin", "completed",
            metadata: new { secret_id = row.SecretId, row.Name }, ct: ct);

        return CreatedAtAction(nameof(Get), new { id = row.SecretId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public async Task<ActionResult<SecretResponse>> Update(
        Guid id, [FromBody] UpdateSecretRequest request, CancellationToken ct)
    {
        var row = await _db.Secrets
            .FirstOrDefaultAsync(s => s.SecretId == id
                                      && s.IsActive, ct);
        if (row is null) return NotFound();

        var before = new { row.Name, row.Description };

        if (request.Description is not null)
            row.Description = request.Description.Trim();

        // Empty string = explicit wipe; null = leave value alone. Keeps
        // "edit description only" flows from clobbering the ciphertext.
        if (request.Value is not null)
        {
            if (string.IsNullOrEmpty(request.Value))
                return Problems.BadRequest(
                    "value cannot be empty — delete the secret to remove it",
                    code: "secret_value_empty");
            row.EncryptedValue = _crypto.Encrypt(request.Value) ?? Array.Empty<byte>();
        }

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("secret", row.SecretId, "update",
            before,
            new { row.Name, row.Description, value_rotated = request.Value is not null },
            ct);
        await _trace.EventAsync("secret.update", "admin", "completed",
            metadata: new { secret_id = row.SecretId, row.Name, value_rotated = request.Value is not null },
            ct: ct);

        return Ok(ToResponse(row));
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _db.Secrets
            .FirstOrDefaultAsync(s => s.SecretId == id
                                      && s.IsActive, ct);
        if (row is null) return NotFound();

        // Soft delete: keeps audit references intact. Name comes free again
        // once IsActive=false, but a re-create will make a fresh row so old
        // audit entries stay traceable to the original SecretId.
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("secret", row.SecretId, "delete",
            new { row.Name }, null, ct);
        await _trace.EventAsync("secret.delete", "admin", "completed",
            metadata: new { secret_id = row.SecretId, row.Name }, ct: ct);

        return NoContent();
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static string? Validate(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || !NameRegex.IsMatch(name))
            return "name must be 2–64 chars, lowercase letters/digits/hyphens/underscores";
        if (string.IsNullOrEmpty(value))
            return "value is required";
        return null;
    }

    private static SecretResponse ToResponse(SecretModel s) => new()
    {
        SecretId = s.SecretId,
        Name = s.Name,
        Description = s.Description,
        HasValue = s.EncryptedValue is { Length: > 0 },
        CreatedBy = s.CreatedBy,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
