using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Outcome of one dispatched tool call. Persisted into agent_runs.tool_calls (jsonb)
// and streamed to the chat UI so the user can see what happened and why.
//
// Error is populated on any non-success: handler exception, classifier denial,
// unknown tool. Result is populated only when the handler ran successfully.
// AskedUser is true when the classifier returned Ask and the dispatcher
// chose not to execute — the agent loop then surfaces the question and
// re-dispatches with an explicit approval flag.
public class ToolResult
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public JsonElement Args { get; set; }

    [JsonPropertyName("decision")]
    public PermissionDecision Decision { get; set; } = new();

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("asked_user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AskedUser { get; set; }
}
