using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class DiffResult
{
    [JsonPropertyName("nodes_added")]
    public List<JsonElement> NodesAdded { get; set; } = new();

    [JsonPropertyName("nodes_removed")]
    public List<JsonElement> NodesRemoved { get; set; } = new();

    [JsonPropertyName("nodes_changed")]
    public List<NodeChange> NodesChanged { get; set; } = new();

    [JsonPropertyName("edges_added")]
    public List<JsonElement> EdgesAdded { get; set; } = new();

    [JsonPropertyName("edges_removed")]
    public List<JsonElement> EdgesRemoved { get; set; } = new();

    [JsonPropertyName("has_changes")]
    public bool HasChanges { get; set; }
}

public class NodeChange
{
    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("before")]
    public JsonElement Before { get; set; }

    [JsonPropertyName("after")]
    public JsonElement After { get; set; }
}
