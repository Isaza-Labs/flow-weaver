using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UserModel = flow_weaver_backend.Models.User;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native create_user tool. There is no UserService (UsersController talks to
// AppDbContext directly), so this handler mirrors that create logic in-process
// using the same injectables. Admin-only in PermissionClassifier → the
// dispatcher refuses a non-admin before this runs.
//
// The plaintext password rides in the tool args (unavoidable for user
// creation); the dispatcher logs only arg *lengths*, not content, and the hash
// is what's persisted.
public sealed class CreateUserHandler : IToolHandler
{
    private static readonly string[] AllowedRoles = { "admin", "operator", "viewer" };

    public string Name => "create_user";

    public string Description =>
        "Tier: elevated_confirm. ADMIN ONLY. Create a user and assign "
        + "a role: admin | operator | viewer (default viewer) — this is how you 'assign "
        + "permissions' at the global level. `password` must satisfy the policy (>= 12 chars, "
        + "upper + lower + digit + symbol, and must not contain the username or email). If the "
        + "user didn't give a password, generate a strong compliant one and tell them. If "
        + "created=false with status_code=403, tell the user this needs the admin role.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["username", "email", "password"],
          "properties": {
            "username": { "type": "string" },
            "email": { "type": "string" },
            "password": { "type": "string", "description": ">=12 chars, upper+lower+digit+symbol, not containing username/email." },
            "role": { "type": "string", "enum": ["admin", "operator", "viewer"], "description": "Defaults to viewer." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<UserModel> _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly ICurrentUser _caller;
    private readonly IBuiltinGrantSync _grants;
    private readonly ILogger<CreateUserHandler> _logger;

    public CreateUserHandler(
        AppDbContext db,
        IPasswordHasher<UserModel> hasher,
        IPasswordPolicy policy,
        ICurrentUser caller,
        IBuiltinGrantSync grants,
        ILogger<CreateUserHandler> logger)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _caller = caller;
        _grants = grants;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var username = ToolArgs.Str(args, "username");
        var email = ToolArgs.Str(args, "email");
        var password = ToolArgs.Str(args, "password");

        if (string.IsNullOrWhiteSpace(username)) return Err(400, "username is required");
        if (string.IsNullOrWhiteSpace(email)) return Err(400, "email is required");
        if (string.IsNullOrWhiteSpace(password)) return Err(400, "password is required");

        var role = (ToolArgs.Str(args, "role") ?? "viewer").ToLowerInvariant();
        if (!AllowedRoles.Contains(role))
            return Err(400, $"role must be one of: {string.Join(", ", AllowedRoles)}");

        // Granular RBAC hardening: creating an admin is admin-only, so a
        // non-admin holding only user.manage (granular mode) can't mint an admin
        // account and escalate through it.
        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            && !_caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase)))
            return Err(403, "only an admin can create an admin user");

        var policy = _policy.Validate(password!, username!, email!);
        if (!policy.IsValid)
            return Err(400, policy.Error ?? "password rejected by policy");

        var duplicate = await _db.Users.AnyAsync(u => u.Username == username, ct);
        if (duplicate)
            return Err(409, "a user with that username already exists");

        var now = DateTime.UtcNow;
        var user = new UserModel
        {
            UserId = Guid.NewGuid(),
            Username = username!,
            Email = email!,
            Role = role,
            PasswordChangedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = _hasher.HashPassword(user, password!);

        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(ct);
            // Dual-write: mirror the new user's role into the built-in grants so
            // they have their permissions from creation (parity with
            // UsersController.Post). Without this a user created via the agent
            // would hold no grants → default-denied in granular mode.
            await _grants.SyncUserAsync(user.UserId, user.Role, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.create_user.failed username={Username}", username);
            return Err(500, $"failed to create user: {ex.Message}");
        }

        _logger.LogInformation("ai.tool.create_user.ok user_id={UserId} role={Role}", user.UserId, user.Role);
        return JsonSerializer.SerializeToElement(new
        {
            created = true,
            user_id = user.UserId,
            username = user.Username,
            email = user.Email,
            role = user.Role,
        });
    }

    private static JsonElement Err(int status, string error) =>
        JsonSerializer.SerializeToElement(new { created = false, status_code = status, error });
}
