using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Integration;

// Executes a single HTTP probe against an integration and updates its
// Status + LastCheckedAt in the DB. Returns the wire-format result.
// The controller is a passthrough.
public interface IIntegrationHealthChecker
{
    Task<ActionResult<IntegrationHealthCheckResult>> CheckAsync(Guid integrationId, CancellationToken ct);
}
