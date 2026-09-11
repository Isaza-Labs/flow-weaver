using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Lightweight row used by GET /api/admin/reports — never includes the
// blob or the decrypted prompt so the list view stays small and fast and
// reading it doesn't trigger a read_prompt audit row.
public class ReportArtifactSummary
{
    [JsonPropertyName("report_artifact_id")]
    public Guid ReportArtifactId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public int SizeBytes { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    /// <summary>
    /// Resolved at query time via a join on Users, so a user rename shows
    /// the current name without touching the persisted row.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("agent_conversation_id")]
    public Guid? AgentConversationId { get; set; }

    [JsonPropertyName("workflow_run_id")]
    public Guid? WorkflowRunId { get; set; }

    /// <summary>
    /// How the producing workflow run was triggered ("manual" | "schedule" |
    /// "webhook" | "subflow"), resolved at query time via a join on
    /// workflow_runs. Null for non-workflow artifacts or purged runs. Lets the
    /// UI attribute schedule-triggered documents to the schedule when there is
    /// no acting user.
    /// </summary>
    [JsonPropertyName("run_trigger")]
    public string? RunTrigger { get; set; }

    [JsonPropertyName("agent_prompt_redacted")]
    public bool AgentPromptRedacted { get; set; }

    [JsonPropertyName("has_agent_prompt")]
    public bool HasAgentPrompt { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
