using System.Text.Json;

namespace flow_weaver_backend.Models;

public class WorkflowVersion : BaseModel
{
    public Guid WorkflowVersionId { get; set; }
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    public JsonElement Nodes { get; set; } = default;
    public JsonElement Edges { get; set; } = default;
    public JsonElement Services { get; set; } = default;
    public string ChangeSummary { get; set; } = string.Empty;
    public DateTime PromotedAt { get; set; }
    public Guid? ConversationId { get; set; }
    public string PromotedBy { get; set; } = string.Empty;
    public Guid? TestRunId { get; set; }
}
