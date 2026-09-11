using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Git;

// CRUD over inbound Git webhook receivers. The actual receiver endpoint
// (POST /api/git/webhooks/{id}) lives on a separate controller because
// it needs to be public (no [Authorize]) and look up rows without an
// ICurrentUser context.
public interface IGitWebhookService
{
    Task<ActionResult<ListResponse<GitWebhookResponse>>> ListAsync(
        Guid repositoryId, CancellationToken ct);

    Task<ActionResult<GitWebhookResponse>> GetAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct);

    Task<ActionResult<GitWebhookResponse>> CreateAsync(
        Guid repositoryId, CreateGitWebhook dto, CancellationToken ct);

    Task<ActionResult<GitWebhookResponse>> UpdateAsync(
        Guid repositoryId, Guid webhookId, UpdateGitWebhook dto, CancellationToken ct);

    Task<ActionResult<GitWebhookResponse>> DeleteAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct);

    Task<ActionResult<ListResponse<GitWebhookDeliveryResponse>>> ListDeliveriesAsync(
        Guid repositoryId, Guid webhookId, int limit, CancellationToken ct);
}
