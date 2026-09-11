using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateInventorySource
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }

    [JsonPropertyName("mapping")]
    public JsonElement? Mapping { get; set; }

    [JsonPropertyName("sync_mode")]
    public string? SyncMode { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
