using System.Security.Claims;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Admin-only CRUD for users. Passwords are validated through
// IPasswordPolicy at create time, and the role taken from the request
// body is checked against AllowedRoles before it is persisted.
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Admin")]
public class UsersController : ControllerBase
{
    private static readonly string[] AllowedRoles = { "admin", "operator", "viewer" };

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly IBuiltinGrantSync _grants;
    private readonly IAuditLogger _audit;

    public UsersController(
        AppDbContext db, IPasswordHasher<User> hasher, IPasswordPolicy policy,
        IBuiltinGrantSync grants, IAuditLogger audit)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _grants = grants;
        _audit = audit;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<UserResponse>>> Get(CancellationToken ct)
    {
        var users = await _db.Users
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        return new OkObjectResult(users.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<UserResponse>> GetById(Guid id, CancellationToken ct)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);

        if (user is null)
            return Problems.NotFound("user", id);

        return ToResponse(user);
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<UserResponse>> Post([FromBody] CreateUser dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Username))
            return Problems.BadRequest("username is required", code: "username_required");
        if (string.IsNullOrWhiteSpace(dto.Email))
            return Problems.BadRequest("email is required", code: "email_required");

        var role = (dto.Role ?? "viewer").ToLowerInvariant();
        if (!AllowedRoles.Contains(role))
            return Problems.BadRequest(
                $"role must be one of: {string.Join(", ", AllowedRoles)}",
                code: "role_invalid");

        var policy = _policy.Validate(dto.Password, dto.Username, dto.Email);
        if (!policy.IsValid)
            return Problems.BadRequest(policy.Error ?? "password rejected", code: "password_policy");


        var duplicate = await _db.Users
            .AnyAsync(u => u.Username == dto.Username, ct);
        if (duplicate)
            return Problems.Conflict("username already exists", code: "username_taken");

        var now = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = dto.Username,
            Email = dto.Email,
            Role = role,
            PasswordChangedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = _hasher.HashPassword(user, dto.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        // Dual-write: mirror the new user's role into the built-in grants so
        // the granular model tracks the legacy role from creation.
        await _grants.SyncUserAsync(user.UserId, user.Role, ct);

        // Account lifecycle is privilege lifecycle: creating a user, changing
        // its role and deactivating it are the three events an auditor asks
        // for first, and none of them were recorded anywhere.
        await _audit.LogAsync("user", user.UserId, "create",
            after: new { user.Username, user.Email, user.Role }, ct: ct);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Users",
            routeValues: new { id = user.UserId },
            value: ToResponse(user));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<UserResponse>> Update(
        Guid id, [FromBody] UpdateUser dto, CancellationToken ct)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);

        if (user is null)
            return Problems.NotFound("user", id);

        // Snapshot before the dto lands — `user` is tracked.
        var auditBefore = new { user.Username, user.Email, user.Role, user.IsActive };

        if (dto.Email is not null) user.Email = dto.Email;
        var roleChanged = false;
        if (dto.Role is not null)
        {
            var role = dto.Role.ToLowerInvariant();
            if (!AllowedRoles.Contains(role))
                return Problems.BadRequest(
                    $"role must be one of: {string.Join(", ", AllowedRoles)}",
                    code: "role_invalid");
            roleChanged = !string.Equals(user.Role, role, StringComparison.Ordinal);
            user.Role = role;
        }
        if (dto.IsActive is not null) user.IsActive = dto.IsActive.Value;

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Dual-write: keep the built-in grant membership in step with the role.
        if (roleChanged)
            await _grants.SyncUserAsync(user.UserId, user.Role, ct);

        // A privilege escalation gets its own action so it can be alerted on
        // without parsing before/after payloads.
        await _audit.LogAsync("user", user.UserId,
            roleChanged ? "user.role_changed" : "update",
            before: auditBefore,
            after: new { user.Username, user.Email, user.Role, user.IsActive }, ct: ct);

        return ToResponse(user);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<UserResponse>> Delete(Guid id, CancellationToken ct)
    {
        // Can't delete yourself — prevents locking out the last admin by accident.
        if (id == CurrentUserId())
            return Problems.BadRequest("cannot delete your own user", code: "self_delete");

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);

        if (user is null)
            return Problems.NotFound("user", id);

        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("user", user.UserId, "delete",
            before: new { user.Username, user.Email, user.Role }, ct: ct);

        return ToResponse(user);
    }

    // ─── helpers ────────────────────────────────────────────────────
    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static UserResponse ToResponse(User u) => new()
    {
        UserId = u.UserId,
        Username = u.Username,
        Email = u.Email,
        Role = u.Role,
        IsActive = u.IsActive,
        Locked = u.LockedUntil is { } until && until > DateTime.UtcNow,
        PasswordChangedAt = u.PasswordChangedAt,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };
}
