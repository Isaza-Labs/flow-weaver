using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class InventorySourceResponse
{
    [JsonPropertyName("inventory_source_id")]
    public Guid InventorySourceId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; set; }

    [JsonPropertyName("mapping")]
    public JsonElement Mapping { get; set; }

    [JsonPropertyName("sync_mode")]
    public string SyncMode { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("last_sync_at")]
    public DateTime? LastSyncAt { get; set; }

    [JsonPropertyName("last_sync_status")]
    public string? LastSyncStatus { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
