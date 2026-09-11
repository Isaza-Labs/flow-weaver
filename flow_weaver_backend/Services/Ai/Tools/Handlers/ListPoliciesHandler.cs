using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native list_policies tool — reads the governance policies via
// IPolicy in-process. Viewer-readable (matches the controller's GET).
public sealed class ListPoliciesHandler : IToolHandler
{
    public string Name => "list_policies";

    public string Description =>
        "Tier: autonomous. List the governance policies (id, name, enabled, rule). "
        + "Use before create_policy to avoid duplicates or to reference an existing gate.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """).RootElement.Clone();

    private readonly IPolicy _policies;
    private readonly ILogger<ListPoliciesHandler> _logger;

    public ListPoliciesHandler(IPolicy policies, ILogger<ListPoliciesHandler> logger)
    {
        _policies = policies;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        ServiceOutcome<ListResponse<PolicyResponse>> outcome;
        try
        {
            outcome = ToolResults.Read(await _policies.GetAsync(200, 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.list_policies.failed");
            return JsonSerializer.SerializeToElement(new { error = $"failed: {ex.Message}" });
        }

        if (!outcome.Ok)
            return JsonSerializer.SerializeToElement(new { error = outcome.Error ?? "could not list policies", status_code = outcome.Status });

        var policies = outcome.Value!.Data.Select(p => new
        {
            policy_id = p.PolicyId,
            name = p.Name,
            description = p.Description,
            enabled = p.Enabled,
            rule = p.Rule,
        });
        return JsonSerializer.SerializeToElement(new { policies });
    }
}
