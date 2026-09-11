using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateGitWebhook
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("provider")] public string Provider { get; set; } = "github";
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("on_push_workflow_id")] public Guid? OnPushWorkflowId { get; set; }
    [JsonPropertyName("on_push_branches")] public List<string> OnPushBranches { get; set; } = new();
    [JsonPropertyName("auto_pull")] public bool AutoPull { get; set; } = true;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("allow_unsigned")] public bool AllowUnsigned { get; set; } = false;
}

public class UpdateGitWebhook
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    // null = leave existing secret. Empty string = clear (no verification).
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("on_push_workflow_id")] public Guid? OnPushWorkflowId { get; set; }
    [JsonPropertyName("on_push_branches")] public List<string>? OnPushBranches { get; set; }
    [JsonPropertyName("auto_pull")] public bool? AutoPull { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool? AllowUnsigned { get; set; }
}

public class GitWebhookResponse
{
    [JsonPropertyName("git_webhook_id")] public Guid GitWebhookId { get; set; }
    [JsonPropertyName("git_repository_id")] public Guid GitRepositoryId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("provider")] public string Provider { get; set; } = "github";
    [JsonPropertyName("has_secret")] public bool HasSecret { get; set; }
    [JsonPropertyName("on_push_workflow_id")] public Guid? OnPushWorkflowId { get; set; }
    [JsonPropertyName("on_push_branches")] public List<string> OnPushBranches { get; set; } = new();
    [JsonPropertyName("auto_pull")] public bool AutoPull { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool AllowUnsigned { get; set; }
    [JsonPropertyName("last_delivery_at")] public DateTime? LastDeliveryAt { get; set; }
    [JsonPropertyName("last_delivery_status")] public string? LastDeliveryStatus { get; set; }
    // The public ingestion URL the user copies into GitHub/GitLab.
    // Built from the HTTP request host so it works behind reverse proxies
    // without needing the operator to configure a base URL.
    [JsonPropertyName("ingestion_url")] public string? IngestionUrl { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class GitWebhookDeliveryResponse
{
    [JsonPropertyName("git_webhook_delivery_id")] public Guid GitWebhookDeliveryId { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("commit_sha")] public string? CommitSha { get; set; }
    [JsonPropertyName("workflow_run_id")] public Guid? WorkflowRunId { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
