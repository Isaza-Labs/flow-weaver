using System.Text.Json;

namespace flow_weaver_backend.Models;

public class AgentRun : BaseModel
{
    public Guid AgentRunId { get; set; }
    public Guid? ConversationId { get; set; }
    public string TraceId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public Guid? PlanId { get; set; }
    public Guid? ParentAgentRunId { get; set; }
    public JsonElement ToolCalls { get; set; } = default;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public double CostEstimateUSD { get; set; }
    public string Status { get; set; } = AgentRunStatus.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
