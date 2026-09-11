using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// Base CRUD contract every entity service implements. Uses DTOs on the wire:
//   - TResponse is returned to clients (no DB-internal or encrypted fields)
//   - TCreate is the POST body
//   - TUpdate is the PUT body (usually all-nullable so clients can PATCH)
// Ids are Guid across the codebase.
//
// GetAsync is paginated: `limit` defaults to 50, max 200 (enforced in each
// service). `offset` defaults to 0. Total is COUNT(*) (Q10 resolution).
public interface IBaseService<TResponse, TCreate, TUpdate>
{
    Task<ActionResult<ListResponse<TResponse>>> GetAsync(int limit = 50, int offset = 0);

    Task<ActionResult<TResponse>> GetByIdAsync(Guid id);

    Task<ActionResult<TResponse>> PostAsync(TCreate dto);

    Task<ActionResult<TResponse>> UpdateAsync(Guid id, TUpdate dto);

    Task<ActionResult<TResponse>> DeleteAsync(Guid id);
}
