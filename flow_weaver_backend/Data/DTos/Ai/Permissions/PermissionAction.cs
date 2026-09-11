using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Result of classifying a tool call. The dispatcher uses this to decide
// whether to execute, prompt the user, or refuse outright.
// Serializes as the lowercase string ("allow" / "ask" / "deny") to match
// the Go wire format and the values persisted in agent_runs.tool_calls jsonb.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionAction
{
    [JsonStringEnumMemberName("allow")]
    Allow,

    [JsonStringEnumMemberName("ask")]
    Ask,

    [JsonStringEnumMemberName("deny")]
    Deny,
}
