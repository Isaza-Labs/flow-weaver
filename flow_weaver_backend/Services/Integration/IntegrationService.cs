using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Integration;

public class IntegrationService : IIntegration
{
    private readonly IIntegrationRepository _integrations;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly IResourcePermissionService _permissions;
    private readonly flow_weaver_backend.Services.Settings.IAppSettingsService _appSettings;
    private readonly IIntegrationOAuthTokenService _oauthTokens;
    private readonly ILogger<IntegrationService> _logger;

    public IntegrationService(
        IIntegrationRepository integrations,
        ICurrentUser caller,
        IAuditLogger audit,
        ITraceLogger trace,
        IResourcePermissionService permissions,
        flow_weaver_backend.Services.Settings.IAppSettingsService appSettings,
        IIntegrationOAuthTokenService oauthTokens,
        ILogger<IntegrationService> logger)
    {
        _integrations = integrations;
        _caller = caller;
        _audit = audit;
        _trace = trace;
        _permissions = permissions;
        _appSettings = appSettings;
        _oauthTokens = oauthTokens;
        _logger = logger;
    }

    // See WorkflowService.AuthorizeAsync — same opt-in shape. While
    // permissions_granular_gating_enabled is off in /admin/settings we
    // pass through (existing behaviour); when on, the per-integration
    // grants are consulted for the required role.
    private async Task<bool> AuthorizeAsync(Guid integrationId, string requiredRole)
    {
        var settings = await _appSettings.GetAsync();
        if (!settings.PermissionsGranularGatingEnabled) return true;
        return await _permissions.HasAtLeastAsync(
            ResourceTypes.Integration, integrationId, requiredRole, CancellationToken.None);
    }

    public async Task<ActionResult<ListResponse<IntegrationResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _integrations.CountAsync();
        var integrations = await _integrations.ListAsync(limit, offset);

        _logger.LogDebug(
            "integration.list.ok total={Total} returned={Returned}",
            total, integrations.Count);

        return new OkObjectResult(new ListResponse<IntegrationResponse>
        {
            Data = integrations.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<IntegrationResponse>> GetByIdAsync(Guid id)
    {
        var integration = await _integrations.GetByIdAsync(id);
        if (integration is null)
        {
            _logger.LogWarning("integration.get.not_found integration_id={IntegrationId}", id);
            return new NotFoundObjectResult(new { error = "integration not found" });
        }

        return ToResponse(integration);
    }

    public async Task<ActionResult<IntegrationResponse>> PostAsync(CreateIntegration dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("integration.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Type))
        {
            _logger.LogWarning("integration.create.validation_failed reason=type_required");
            return new BadRequestObjectResult(new { error = "type is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.BaseURL))
        {
            _logger.LogWarning("integration.create.validation_failed reason=base_url_required");
            return new BadRequestObjectResult(new { error = "base_url is required" });
        }

        var integration = BuildEntity(dto, await _integrations.ListTakenSlugsAsync());
        _integrations.Add(integration);
        try
        {
            await _integrations.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "integration.create.failed integration_name={IntegrationName}", integration.Name);
            throw;
        }

        await _audit.LogAsync("integration", integration.IntegrationId, "create",
            after: new { integration.Name, integration.Type, integration.BaseURL });
        if (integration.AllowPrivateNetwork)
        {
            // Dedicated audit row so /admin/audit can filter every
            // activation of the SSRF-guard opt-out, regardless of which
            // mutation caused it. UI uses category="allow-private-network".
            await _audit.LogAsync("integration", integration.IntegrationId,
                "allow_private_network.enabled",
                after: new
                {
                    integration_name = integration.Name,
                    integration.Type,
                    integration.BaseURL,
                    reason = "set_on_create",
                });
        }
        await _trace.EventAsync("integration.create", "admin", "completed",
            metadata: new { integration_id = integration.IntegrationId, integration.Name, integration.Type });

        _logger.LogInformation(
            "integration.create.ok integration_id={IntegrationId} integration_name={IntegrationName}",
            integration.IntegrationId, integration.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Integration",
            routeValues: new { id = integration.IntegrationId },
            value: ToResponse(integration));
    }

    public async Task<ActionResult<IntegrationResponse>> UpdateAsync(Guid id, UpdateIntegration dto)
    {
        var integration = await _integrations.GetByIdAsync(id);
        if (integration is null)
        {
            _logger.LogWarning("integration.update.not_found integration_id={IntegrationId}", id);
            return new NotFoundObjectResult(new { error = "integration not found" });
        }

        if (!await AuthorizeAsync(id, ResourceRoles.Editor))
        {
            _logger.LogWarning(
                "integration.update.forbidden integration_id={IntegrationId} user_id={UserId} reason=missing_editor_grant",
                id, _caller.UserId);
            return new ObjectResult(new { error = "missing_editor_grant" }) { StatusCode = 403 };
        }

        // Capture pre-mutation state for the audit. `integration` is tracked,
        // so this has to happen before the dto is applied or before == after.
        // AuthConfig/Headers are deliberately excluded — they hold credential
        // material and must never reach the audit payload; `auth_changed`
        // records that a rotation happened without recording the secret.
        var allowPrivateBefore = integration.AllowPrivateNetwork;
        var auditBefore = new
        {
            integration.Name,
            integration.Type,
            integration.BaseURL,
            integration.Enabled,
            integration.TLSSkipVerify,
            allow_private_network = integration.AllowPrivateNetwork,
        };

        if (dto.Name is not null) integration.Name = dto.Name;
        if (dto.Type is not null) integration.Type = dto.Type;
        if (dto.Description is not null) integration.Description = dto.Description;
        if (dto.BaseURL is not null) integration.BaseURL = dto.BaseURL;
        if (dto.AuthConfig is not null)
        {
            integration.AuthConfig = dto.AuthConfig.Value;
            // A rotated client secret (or any auth change) must take effect on
            // the next call — without this, a cached OAuth access token keeps
            // serving under the OLD credentials until its TTL runs out.
            _oauthTokens.Invalidate(integration.IntegrationId);
        }
        if (dto.Headers is not null) integration.Headers = dto.Headers.Value;
        if (dto.TLSSkipVerify is not null) integration.TLSSkipVerify = dto.TLSSkipVerify.Value;
        if (dto.AllowPrivateNetwork is not null) integration.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.HealthCheck is not null) integration.HealthCheck = dto.HealthCheck.Value;
        if (dto.Enabled is not null) integration.Enabled = dto.Enabled.Value;

        integration.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _integrations.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "integration.update.failed integration_id={IntegrationId}", integration.IntegrationId);
            throw;
        }

        await _audit.LogAsync("integration", integration.IntegrationId, "update",
            before: auditBefore,
            after: new
            {
                integration.Name,
                integration.Type,
                integration.BaseURL,
                integration.Enabled,
                integration.TLSSkipVerify,
                allow_private_network = integration.AllowPrivateNetwork,
                auth_changed = dto.AuthConfig is not null,
                headers_changed = dto.Headers is not null,
            });
        if (allowPrivateBefore != integration.AllowPrivateNetwork)
        {
            var action = integration.AllowPrivateNetwork
                ? "allow_private_network.enabled"
                : "allow_private_network.disabled";
            await _audit.LogAsync("integration", integration.IntegrationId,
                action,
                before: new { allow_private_network = allowPrivateBefore },
                after: new
                {
                    allow_private_network = integration.AllowPrivateNetwork,
                    integration_name = integration.Name,
                    integration.Type,
                    integration.BaseURL,
                    // Justification text the operator typed. Stored as-is
                    // in the audit row; null when not provided.
                    reason = string.IsNullOrWhiteSpace(dto.AllowPrivateNetworkReason)
                        ? null
                        : dto.AllowPrivateNetworkReason.Trim(),
                });
        }
        await _trace.EventAsync("integration.update", "admin", "completed",
            metadata: new { integration_id = integration.IntegrationId, integration.Name });

        _logger.LogInformation("integration.update.ok integration_id={IntegrationId}", integration.IntegrationId);
        return ToResponse(integration);
    }

    public async Task<ActionResult<IntegrationResponse>> DeleteAsync(Guid id)
    {
        var integration = await _integrations.GetByIdAsync(id);
        if (integration is null)
        {
            _logger.LogWarning("integration.delete.not_found integration_id={IntegrationId}", id);
            return new NotFoundObjectResult(new { error = "integration not found" });
        }

        if (!await AuthorizeAsync(id, ResourceRoles.Owner))
        {
            _logger.LogWarning(
                "integration.delete.forbidden integration_id={IntegrationId} user_id={UserId} reason=missing_owner_grant",
                id, _caller.UserId);
            return new ObjectResult(new { error = "missing_owner_grant" }) { StatusCode = 403 };
        }

        integration.IsActive = false;
        integration.UpdatedAt = DateTime.UtcNow;
        await _integrations.SaveChangesAsync();

        await _audit.LogAsync("integration", integration.IntegrationId, "delete",
            before: new { integration.Name, integration.Type });
        await _trace.EventAsync("integration.delete", "admin", "completed",
            metadata: new { integration_id = integration.IntegrationId, integration.Name });

        _logger.LogInformation("integration.delete.ok integration_id={IntegrationId}", integration.IntegrationId);
        return ToResponse(integration);
    }

    // Exposed so the bundle endpoint can stage the entity inside its own
    // transaction without going through SaveChangesAsync twice.
    // `takenSlugs` is passed in rather than queried here so a caller creating
    // SEVERAL integrations in one transaction (the bundle endpoint) can add each
    // freshly allocated slug to the set as it goes — otherwise two bundle
    // entries with the same name would both allocate the same slug and the
    // unique index would reject the batch.
    internal IntegrationModel BuildEntity(CreateIntegration dto, IReadOnlySet<string> takenSlugs)
    {
        var now = DateTime.UtcNow;
        return new IntegrationModel
        {
            IntegrationId = Guid.NewGuid(),
            Name = dto.Name,
            // Cross-instance identity, fixed at creation and never rewritten on
            // rename — a shared workflow bundle refers to this integration by it.
            Slug = Services.Common.Slug.Unique(dto.Name, takenSlugs),
            Type = dto.Type,
            Description = dto.Description,
            BaseURL = dto.BaseURL,
            AuthConfig = dto.AuthConfig ?? default,
            Headers = dto.Headers ?? default,
            TLSSkipVerify = dto.TLSSkipVerify,
            AllowPrivateNetwork = dto.AllowPrivateNetwork,
            HealthCheck = dto.HealthCheck ?? default,
            Status = "unknown",
            Enabled = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // AuthConfig intentionally omitted — it carries secrets.
    internal static IntegrationResponse ToResponse(IntegrationModel a) => new()
    {
        IntegrationId = a.IntegrationId,
        Name = a.Name,
        Type = a.Type,
        Description = a.Description,
        BaseURL = a.BaseURL,
        Headers = a.Headers,
        TLSSkipVerify = a.TLSSkipVerify,
        AllowPrivateNetwork = a.AllowPrivateNetwork,
        HealthCheck = a.HealthCheck,
        Status = a.Status,
        LastCheckedAt = a.LastCheckedAt,
        Enabled = a.Enabled,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt,
    };
}
