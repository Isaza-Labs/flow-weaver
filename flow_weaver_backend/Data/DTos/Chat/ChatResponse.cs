using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class ChatResponse
{
    [JsonPropertyName("conversation_id")]
    public Guid ConversationId { get; set; }

    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;

    [JsonPropertyName("agent_name")]
    public string AgentName { get; set; } = string.Empty;

    [JsonPropertyName("tokens_used")]
    public int TokensUsed { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid? WorkflowId { get; set; }

    [JsonPropertyName("workflow_name")]
    public string WorkflowName { get; set; } = string.Empty;

    [JsonPropertyName("routed_to")]
    public string RoutedTo { get; set; } = string.Empty;
}
