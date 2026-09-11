using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class AgentRunResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("trace_id")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("agent_name")]
    public string AgentName { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("plan_id")]
    public Guid? PlanId { get; set; }

    [JsonPropertyName("parent_agent_run_id")]
    public Guid? ParentAgentRunId { get; set; }

    [JsonPropertyName("tool_calls")]
    public JsonElement ToolCalls { get; set; }

    [JsonPropertyName("tokens_in")]
    public int TokensIn { get; set; }

    [JsonPropertyName("tokens_out")]
    public int TokensOut { get; set; }

    [JsonPropertyName("cost_estimate_usd")]
    public double CostEstimateUSD { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
