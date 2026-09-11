using System.Text.Json;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitListWebhooksHandler : IToolHandler
{
    public string Name => "git_list_webhooks";

    public string Description =>
        "List inbound webhook receivers configured for a registered Git " +
        "repository. Each row carries the public ingestion URL the user " +
        "pasted into GitHub/GitLab, the provider, the workflow it triggers " +
        "(if any), and the last delivery's status. Use BEFORE git_create_webhook " +
        "to avoid duplicates, and to answer 'is webhook X working?' questions " +
        "by inspecting last_delivery_status.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id"],
          "properties": {
            "repository_id": {
              "type": "string",
              "format": "uuid",
              "description": "Git repository UUID. Get from git_list_repositories."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitWebhookService _service;

    public GitListWebhooksHandler(IGitWebhookService service)
    {
        _service = service;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });

        var (ok, value, err) = GitToolHelpers.Unwrap(await _service.ListAsync(id.Value, ct));
        if (!ok || value is null) return err;

        return JsonSerializer.SerializeToElement(new
        {
            total = value.Total,
            webhooks = value.Data.Select(w => new
            {
                webhook_id = w.GitWebhookId,
                name = w.Name,
                provider = w.Provider,
                ingestion_url = w.IngestionUrl,
                has_secret = w.HasSecret,
                on_push_workflow_id = w.OnPushWorkflowId,
                on_push_branches = w.OnPushBranches,
                auto_pull = w.AutoPull,
                enabled = w.Enabled,
                last_delivery_at = w.LastDeliveryAt,
                last_delivery_status = w.LastDeliveryStatus,
            }),
        });
    }
}
