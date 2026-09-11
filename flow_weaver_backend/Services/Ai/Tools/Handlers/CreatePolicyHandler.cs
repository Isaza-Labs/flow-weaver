using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native create_policy tool. Calls IPolicy (PolicyService) IN-PROCESS so the
// write runs under the chatting user's ICurrentUser. The tool is classified
// admin-only in PermissionClassifier, and the ToolDispatcher's role gate runs
// BEFORE this handler — so a non-admin never reaches it (the service's own
// [Authorize(Admin)] lives on the controller and is bypassed in-process, which
// is why the dispatcher gate is the enforcement point).
public sealed class CreatePolicyHandler : IToolHandler
{
    public string Name => "create_policy";

    public string Description =>
        "Tier: elevated_confirm. ADMIN ONLY. Create a governance Policy. "
        + "`rule` is a JSON object in the policy DSL. Two actions are supported: "
        + "(1) gate — {\"action\":\"gate\",\"on\":\"promote\",\"from\":\"qa\",\"to\":\"production\","
        + "\"require\":[{\"type\":\"successful_runs\",\"min\":1}]}; require types: successful_runs "
        + "(min, within_days, scope=this_workflow|any_workflow), last_successful_run_within (days, scope), "
        + "successful_snippet_runs (snippet_ids, min, within_days). "
        + "(2) deny — {\"action\":\"deny\",\"reason\":\"...\",\"when\":{\"env\":[\"production\"],\"action\":[\"run\"]}}; "
        + "when keys (all optional, AND-joined): env, action (create|update|run|promote|ssh_exec), device_role, "
        + "device_pool, snippet_type, description_contains, ssh_command_regex. Omit `when` to always match. "
        + "If created=false with status_code=403, tell the user this needs the admin role.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["name", "rule"],
          "properties": {
            "name": { "type": "string" },
            "description": { "type": "string" },
            "rule": { "type": "object", "description": "Policy DSL object — a gate or deny rule (see the tool description)." },
            "enabled": { "type": "boolean", "description": "Defaults to true." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly IPolicy _policies;
    private readonly ILogger<CreatePolicyHandler> _logger;

    public CreatePolicyHandler(IPolicy policies, ILogger<CreatePolicyHandler> logger)
    {
        _policies = policies;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Err("name is required");

        if (!args.TryGetProperty("rule", out var ruleEl) || ruleEl.ValueKind != JsonValueKind.Object)
            return Err("rule is required and must be a JSON object (a gate or deny policy)");

        var dto = new CreatePolicy
        {
            Name = name!,
            Description = Str(args, "description"),
            Rule = ruleEl.Clone(),
            Enabled = Bool(args, "enabled") ?? true,
        };

        ActionResult<PolicyResponse> result;
        try
        {
            result = await _policies.PostAsync(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.create_policy.failed name={Name}", name);
            return Err($"policy creation failed: {ex.Message}");
        }

        if (result.Result is CreatedAtActionResult { Value: PolicyResponse policy })
        {
            _logger.LogInformation("ai.tool.create_policy.ok policy_id={PolicyId} name={Name}", policy.PolicyId, policy.Name);
            return JsonSerializer.SerializeToElement(new
            {
                created = true,
                policy_id = policy.PolicyId,
                name = policy.Name,
                enabled = policy.Enabled,
            });
        }

        if (result.Result is ObjectResult obj)
        {
            var status = obj.StatusCode ?? 400;
            var error = ExtractError(obj.Value) ?? "policy creation failed";
            _logger.LogWarning("ai.tool.create_policy.rejected status={Status} name={Name}", status, name);
            return JsonSerializer.SerializeToElement(new
            {
                created = false,
                status_code = status,
                error,
                hint = status == 403 ? "Creating policies requires the admin role." : null,
            });
        }

        return Err("unexpected policy service result");
    }

    private static JsonElement Err(string error) =>
        JsonSerializer.SerializeToElement(new { created = false, error });

    private static string? ExtractError(object? value)
    {
        if (value is null) return null;
        var el = JsonSerializer.SerializeToElement(value);
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : el.GetRawText();
    }

    private static string? Str(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;
}
