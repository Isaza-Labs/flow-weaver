using flow_weaver_backend.Dtos;
using InventorySourceModel = flow_weaver_backend.Models.InventorySource;

namespace flow_weaver_backend.Services.InventorySource;

// Pulls devices from a NetBox instance and upserts them into the devices table,
// matched first by (SourceId, ExternalId) and then by device name. Throws
// InventorySyncException on a bad config or a failed NetBox request.
public interface INetBoxSyncService
{
    Task<InventorySyncResult> SyncAsync(InventorySourceModel source, bool dryRun, CancellationToken ct);
}

// A sync that could not run. The message is safe to show: it never carries the
// token. StatusCode is the HTTP status the API answers with (400 for a
// configuration problem, 502 when NetBox itself failed).
public sealed class InventorySyncException : Exception
{
    public int StatusCode { get; }

    public InventorySyncException(string message, int statusCode = 400) : base(message)
    {
        StatusCode = statusCode;
    }
}
