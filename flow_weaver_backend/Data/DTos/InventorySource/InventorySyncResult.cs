using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Outcome of POST /api/inventorysource/{id}/sync.
public class InventorySyncResult
{
    [JsonPropertyName("created")] public int Created { get; set; }
    [JsonPropertyName("updated")] public int Updated { get; set; }
    [JsonPropertyName("unchanged")] public int Unchanged { get; set; }
    // Records the sync could not turn into a row of their own: no usable name, or a
    // name a *different* NetBox device from this source already carries. Reported
    // instead of aborting the sync — see NetBoxSyncService.
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("dry_run")] public bool DryRun { get; set; }
}
