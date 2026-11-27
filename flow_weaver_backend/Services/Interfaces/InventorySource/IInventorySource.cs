using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

public interface IInventorySource : IBaseService<InventorySourceResponse, CreateInventorySource, UpdateInventorySource>
{
    // Pulls the source's devices into the inventory (NetBox only). A dry run
    // reports what would change and writes nothing.
    Task<ActionResult<InventorySyncResult>> SyncAsync(Guid id, bool dryRun, CancellationToken ct = default);
}
