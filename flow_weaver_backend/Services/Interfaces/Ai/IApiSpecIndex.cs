using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Interfaces;

// In-memory index of ApiOperation records parsed from ai_api_specs.
// Backs the agent's discover / detail / execute tools.
public interface IApiSpecIndex
{
    // Re-reads ai_api_specs and rebuilds the cache.
    Task ReloadAsync(CancellationToken ct = default);

    IReadOnlyList<ApiOperation> All();

    // Case-insensitive substring match on operationId / summary / path / tags.
    // api and method filters are optional equality checks.
    IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null);

    ApiOperation? GetByOperationId(string operationId);
}
