using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

public interface IIntegrationAction : IBaseService<IntegrationActionResponse, CreateIntegrationAction, UpdateIntegrationAction>
{
    // Integration actions are owned by an integration, so the canonical
    // create route is POST /api/integration/{integrationId}/actions.
    // PostAsync (inherited) returns 400 pointing callers here.
    Task<ActionResult<IntegrationActionResponse>> PostForIntegrationAsync(Guid integrationId, CreateIntegrationAction dto);
}
