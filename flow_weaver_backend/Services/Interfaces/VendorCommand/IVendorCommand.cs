using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// CRUD over the vendor-command catalog. Inherits the standard paginated
// list / by-id / create / update / delete contract from IBaseService.
// We add a device_type filter on the list because the
// admin UI is keyed off device_type and an unfiltered list of 1000+
// rows across vendors would be unusable.
public interface IVendorCommand : IBaseService<VendorCommandResponse, CreateVendorCommand, UpdateVendorCommand>
{
    // Same as GetAsync but filtered by device_type. When `deviceType`
    // is null/empty, behaves like GetAsync.
    Task<ActionResult<ListResponse<VendorCommandResponse>>> GetAsync(
        string? deviceType, int limit = 100, int offset = 0);
}
