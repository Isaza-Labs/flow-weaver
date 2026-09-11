using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Permission;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native grant_resource_permission tool — grants a user a per-resource role
// (owner | editor | runner | viewer) on a workflow or integration, via
// IResourcePermissionService in-process (which stamps GrantedBy from
// ICurrentUser and is idempotent). Admin only. This is the fine-grained
// half of "assign permissions" (the global-role half is set_user_role).
public sealed class GrantResourcePermissionHandler : IToolHandler
{
    public string Name => "grant_resource_permission";

    public string Description =>
        "Tier: elevated_confirm. ADMIN ONLY. Grant a user a per-resource role on a specific "
        + "workflow or integration. `resource_type`: workflow | integration. `role`: owner | "
        + "editor | runner | viewer. Resolve the user_id via list_users and the resource id via "
        + "list_workflows / list_integrations first. Idempotent.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["resource_type", "resource_id", "user_id", "role"],
          "properties": {
            "resource_type": { "type": "string", "enum": ["workflow", "integration"] },
            "resource_id": { "type": "string", "format": "uuid" },
            "user_id": { "type": "string", "format": "uuid", "description": "The subject user to grant access to." },
            "role": { "type": "string", "enum": ["owner", "editor", "runner", "viewer"] }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly IResourcePermissionService _perms;
    private readonly ILogger<GrantResourcePermissionHandler> _logger;

    public GrantResourcePermissionHandler(
        IResourcePermissionService perms, ILogger<GrantResourcePermissionHandler> logger)
    {
        _perms = perms;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var resourceType = ToolArgs.Str(args, "resource_type");
        var resourceId = ToolArgs.GuidVal(args, "resource_id");
        var userId = ToolArgs.GuidVal(args, "user_id");
        var role = ToolArgs.Str(args, "role");

        if (string.IsNullOrWhiteSpace(resourceType)) return Err("resource_type is required (workflow|integration)");
        if (resourceId is null) return Err("resource_id (uuid) is required");
        if (userId is null) return Err("user_id (uuid) is required");
        if (string.IsNullOrWhiteSpace(role)) return Err("role is required (owner|editor|runner|viewer)");

        var dto = new GrantResourcePermissionRequest
        {
            SubjectType = "user",
            SubjectId = userId.Value,
            Role = role!,
        };

        try
        {
            var resp = await _perms.GrantAsync(resourceType!, resourceId.Value, dto, ct);
            _logger.LogInformation(
                "ai.tool.grant_resource_permission.ok resource_type={ResourceType} resource_id={ResourceId} subject_id={SubjectId} role={Role}",
                resp.ResourceType, resp.ResourceId, resp.SubjectId, resp.Role);
            return JsonSerializer.SerializeToElement(new
            {
                granted = true,
                resource_permission_id = resp.ResourcePermissionId,
                resource_type = resp.ResourceType,
                resource_id = resp.ResourceId,
                user_id = resp.SubjectId,
                role = resp.Role,
            });
        }
        catch (ArgumentException ex)
        {
            // Unknown resource_type / role / bad subject — a validation error.
            return Err(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.grant_resource_permission.failed resource_type={ResourceType}", resourceType);
            return Err($"grant failed: {ex.Message}");
        }
    }

    private static JsonElement Err(string error) =>
        JsonSerializer.SerializeToElement(new { granted = false, error });
}
