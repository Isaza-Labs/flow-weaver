using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native create_snippet tool. Previously the agent could only create snippets
// by calling the self-API through execute_operation, whose bearer is resolved
// from ${secret:session:current:jwt} — a path that carries the EFFECTIVE role
// (capped by a messaging channel's MaxRole, and dependent on the request
// bearer being borrowable). That made admin-only options like
// network_enabled=true fail with an opaque 403 even for admins.
//
// This handler calls ISnippet (SnippetService) IN-PROCESS, in the same scope
// as the chat turn, so SnippetService._caller is the chatting user's real
// ICurrentUser. The admin role therefore propagates natively for interactive
// web chat, and the service's own RBAC (network_enabled = admin-only) still
// applies — returning a clean, structured reason the agent can relay verbatim
// instead of a raw HTTP 403.
public sealed class CreateSnippetHandler : IToolHandler, IArgumentSensitiveTier
{
    public string Name => "create_snippet";

    // network_enabled=true is not "create a snippet" — it is "create a snippet
    // that runs with the sandbox's network isolation lifted (--share-net) and
    // the extended import allow-list (socket, paramiko, netmiko)". That is the
    // single largest capability the agent can hand itself.
    //
    // The service-level RBAC already restricts it to admins, but that is the
    // wrong boundary on its own: everything the agent reads is untrusted input
    // (SSH output, a git file, an MCP tool result, a Telegram message), so an
    // admin's chat session is exactly where a prompt injection would aim. Role
    // checks don't help when the role is the attacker's chosen vehicle.
    //
    // human_only means the agent refuses regardless of role and tells the user
    // to do it from the Snippets UI — where a human sees the flag being set.
    // Everything else about create_snippet stays single_confirm.
    public string? EscalatedTier(JsonElement args) =>
        Bool(args, "network_enabled") == true
            ? PermissionClassifier.TierHumanOnly
            : null;

    public string Description =>
        "Tier: single_confirm. Create a reusable Snippet (task): ping, rest_call, "
        + "transform, python_snippet, ssh, etc. Runs as the current user, so the "
        + "service RBAC applies. network_enabled=true (lifts the python sandbox's "
        + "network isolation for netmiko/paramiko SSH) is human_only and will ALWAYS "
        + "be refused here, admin or not — do not call this tool with it. Tell the "
        + "user to create that snippet from the Snippets UI, where the flag is set by "
        + "a human; do NOT retry. Emit a single batch Plan block before calling.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["name", "type", "target_mode"],
          "properties": {
            "name": { "type": "string" },
            "type": { "type": "string", "description": "ping | rest_call | transform | python_snippet | ssh | ..." },
            "description": { "type": "string" },
            "code": { "type": "string", "description": "Body matching the type (Python, JMESPath, CLI, or rest_call JSON)." },
            "script_language": { "type": "string", "description": "python | bash | null" },
            "input_schema": { "type": "object" },
            "output_schema": { "type": "object" },
            "target_mode": { "type": "string", "description": "once | per_device | per_pool" },
            "max_parallel": { "type": "integer" },
            "timeout_seconds": { "type": "integer" },
            "logic_diagram_mermaid": { "type": "string", "description": "Required for python_snippet and transform." },
            "idempotency": { "type": "string", "description": "idempotent | requires_compensation | non_reversible" },
            "changes_state": { "type": "boolean", "description": "Whether a step running this snippet CHANGES anything, which is a different question from idempotency: that one says whether an action could be undone, this says whether anything was done. REQUIRED for python_snippet, whose code lives on this row so the handler cannot tell. Omitting it there means every step fails until a node declares config_overrides.changes." },
            "network_enabled": { "type": "boolean", "description": "python_snippet ONLY; requires the admin role. Lifts sandbox network isolation." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly ISnippet _snippets;
    private readonly ILogger<CreateSnippetHandler> _logger;

    public CreateSnippetHandler(ISnippet snippets, ILogger<CreateSnippetHandler> logger)
    {
        _snippets = snippets;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var dto = new CreateSnippet
        {
            Name = Str(args, "name") ?? string.Empty,
            Type = Str(args, "type") ?? string.Empty,
            Description = Str(args, "description"),
            Code = Str(args, "code"),
            ScriptLanguage = Str(args, "script_language"),
            TargetMode = Str(args, "target_mode") ?? string.Empty,
            LogicDiagramMermaid = Str(args, "logic_diagram_mermaid"),
            Idempotency = Str(args, "idempotency"),
            ChangesState = Bool(args, "changes_state"),
            InputSchema = Obj(args, "input_schema"),
            OutputSchema = Obj(args, "output_schema"),
            MaxParallel = Int(args, "max_parallel"),
            TimeoutSeconds = Int(args, "timeout_seconds"),
            NetworkEnabled = Bool(args, "network_enabled"),
        };

        ActionResult<SnippetResponse> result;
        try
        {
            result = await _snippets.PostAsync(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.create_snippet.failed type={Type}", dto.Type);
            return JsonSerializer.SerializeToElement(new { created = false, error = $"snippet creation failed: {ex.Message}" });
        }

        // Success = CreatedAtActionResult carrying the SnippetResponse.
        if (result.Result is CreatedAtActionResult { Value: SnippetResponse snippet })
        {
            _logger.LogInformation(
                "ai.tool.create_snippet.ok snippet_id={SnippetId} type={Type} network_enabled={Net}",
                snippet.SnippetId, snippet.Type, snippet.NetworkEnabled);
            return JsonSerializer.SerializeToElement(new
            {
                created = true,
                snippet_id = snippet.SnippetId,
                name = snippet.Name,
                type = snippet.Type,
                network_enabled = snippet.NetworkEnabled,
            });
        }

        // Everything else is an error ObjectResult with a { error } body. Pass
        // status + message through so the agent relays the real reason (e.g.
        // 403 network_enabled admin-only) rather than a generic failure.
        if (result.Result is ObjectResult obj)
        {
            var status = obj.StatusCode ?? 400;
            var error = ExtractError(obj.Value) ?? "snippet creation failed";
            _logger.LogWarning(
                "ai.tool.create_snippet.rejected status={Status} type={Type}", status, dto.Type);
            return JsonSerializer.SerializeToElement(new
            {
                created = false,
                status_code = status,
                error,
                hint = status == 403
                    ? "This requires the admin role. Ask the user to create it from the Snippets UI or with an admin account; do not retry."
                    : null,
            });
        }

        return JsonSerializer.SerializeToElement(new { created = false, error = "unexpected snippet service result" });
    }

    private static string? ExtractError(object? value)
    {
        if (value is null) return null;
        // The service returns an anonymous `{ error = "..." }`; round-trip
        // through JSON so we don't depend on the anonymous type shape.
        var el = JsonSerializer.SerializeToElement(value);
        return el.ValueKind == JsonValueKind.Object
               && el.TryGetProperty("error", out var e)
               && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : el.GetRawText();
    }

    private static string? Str(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool? Bool(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;

    private static JsonElement? Obj(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? v.Clone() : null;
}
