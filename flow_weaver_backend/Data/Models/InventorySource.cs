using System.Text.Json;

namespace flow_weaver_backend.Models;

public class InventorySource : BaseModel
{
    public Guid InventorySourceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement Config { get; set; } = default;
    public JsonElement Mapping { get; set; } = default;
    public string SyncMode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
}
