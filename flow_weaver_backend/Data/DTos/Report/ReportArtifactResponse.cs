using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Detail view. Includes the base64 blob (decompressed on the way out if
// the DB row was gzipped) and the decrypted agent prompt. Only admins
// hit this; every call leaves an audit row "report.read_prompt".
public class ReportArtifactResponse
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

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Base64 of the ORIGINAL file bytes (already ungzipped when applicable).
    /// </summary>
    [JsonPropertyName("base64")]
    public string Base64 { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("agent_conversation_id")]
    public Guid? AgentConversationId { get; set; }

    [JsonPropertyName("agent_run_id")]
    public Guid? AgentRunId { get; set; }

    [JsonPropertyName("agent_name")]
    public string? AgentName { get; set; }

    /// <summary>
    /// Decrypted on the way out, only for admins. Regex-redacted values
    /// come back with [REDACTED:*] placeholders in situ.
    /// </summary>
    [JsonPropertyName("agent_prompt")]
    public string? AgentPrompt { get; set; }

    [JsonPropertyName("agent_prompt_redacted")]
    public bool AgentPromptRedacted { get; set; }

    [JsonPropertyName("workflow_run_id")]
    public Guid? WorkflowRunId { get; set; }

    /// <summary>
    /// How the producing workflow run was triggered ("manual" | "schedule" |
    /// "webhook" | "subflow"). Null for non-workflow artifacts or purged runs.
    /// </summary>
    [JsonPropertyName("run_trigger")]
    public string? RunTrigger { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid? WorkflowId { get; set; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
