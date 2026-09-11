using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native set_user_role tool — changes a user's global RBAC role
// (admin|operator|viewer). Admin only.
public sealed class SetUserRoleHandler : IToolHandler
{
    private static readonly string[] AllowedRoles = { "admin", "operator", "viewer" };

    public string Name => "set_user_role";

    public string Description =>
        "Tier: elevated_confirm. ADMIN ONLY. Change a user's global role to admin | operator | "
        + "viewer (resolve the user_id via list_users first). This is how you re-assign a user's "
        + "global permissions.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["user_id", "role"],
          "properties": {
            "user_id": { "type": "string", "format": "uuid" },
            "role": { "type": "string", "enum": ["admin", "operator", "viewer"] }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly IBuiltinGrantSync _grants;
    private readonly ILogger<SetUserRoleHandler> _logger;

    public SetUserRoleHandler(
        AppDbContext db, ICurrentUser caller, IBuiltinGrantSync grants, ILogger<SetUserRoleHandler> logger)
    {
        _db = db;
        _caller = caller;
        _grants = grants;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = ToolArgs.GuidVal(args, "user_id");
        if (id is null) return Err(400, "user_id (uuid) is required");

        var role = (ToolArgs.Str(args, "role") ?? string.Empty).ToLowerInvariant();
        if (!AllowedRoles.Contains(role))
            return Err(400, $"role must be one of: {string.Join(", ", AllowedRoles)}");

        // Granular RBAC hardening: minting an admin is admin-only. Otherwise a
        // non-admin who merely holds the user.manage capability (granular mode)
        // could self-promote to admin via the agent. admin is the superuser tier
        // and must only be conferred by an existing admin.
        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            && !_caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase)))
            return Err(403, "only an admin can grant the admin role");

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.UserId == id.Value && u.IsActive, ct);
        if (user is null) return Err(404, "user not found");

        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
            // Dual-write: mirror the role change into the built-in grants.
            await _grants.SyncUserAsync(user.UserId, role, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.set_user_role.failed user_id={UserId}", id);
            return Err(500, $"failed to set role: {ex.Message}");
        }

        _logger.LogInformation("ai.tool.set_user_role.ok user_id={UserId} role={Role}", user.UserId, role);
        return JsonSerializer.SerializeToElement(new
        {
            updated = true,
            user_id = user.UserId,
            username = user.Username,
            role = user.Role,
        });
    }

    private static JsonElement Err(int status, string error) =>
        JsonSerializer.SerializeToElement(new { updated = false, status_code = status, error });
}
