using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using InventorySourceModel = flow_weaver_backend.Models.InventorySource;

namespace flow_weaver_backend.Services.InventorySource;

public class InventorySourceService : IInventorySource
{
    private readonly IRepository<InventorySourceModel> _sources;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<InventorySourceService> _logger;
    private readonly INetBoxSyncService _netbox;
    private readonly ISecretRepository _secrets;
    private readonly IIntegrationRepository _integrations;
    private readonly ICredentialRepository _credentials;

    public InventorySourceService(
        IRepository<InventorySourceModel> sources,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<InventorySourceService> logger,
        INetBoxSyncService netbox,
        ISecretRepository secrets,
        IIntegrationRepository integrations,
        ICredentialRepository credentials)
    {
        _sources = sources;
        _caller = caller;
        _audit = audit;
        _logger = logger;
        _netbox = netbox;
        _secrets = secrets;
        _integrations = integrations;
        _credentials = credentials;
    }

    public async Task<ActionResult<ListResponse<InventorySourceResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _sources.CountAsync();
        var sources = await _sources.ListAsync(limit, offset);

        _logger.LogDebug(
            "inventory_source.list.ok total={Total} returned={Returned}",
            total, sources.Count);

        return new OkObjectResult(new ListResponse<InventorySourceResponse>
        {
            Data = sources.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<InventorySourceResponse>> GetByIdAsync(Guid id)
    {
        var source = await _sources.GetByIdAsync(id);
        if (source is null)
        {
            _logger.LogWarning("inventory_source.get.not_found inventory_source_id={InventorySourceId}", id);
            return new NotFoundObjectResult(new { error = "inventory_source not found" });
        }

        return ToResponse(source);
    }

    public async Task<ActionResult<InventorySourceResponse>> PostAsync(CreateInventorySource dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("inventory_source.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Type))
        {
            _logger.LogWarning("inventory_source.create.validation_failed reason=type_required");
            return new BadRequestObjectResult(new { error = "type is required" });
        }

        var type = NetBoxSourceConfig.IsNetBox(dto.Type) ? NetBoxSourceConfig.Type : dto.Type;
        var config = dto.Config ?? default;
        if (type == NetBoxSourceConfig.Type)
        {
            var (normalized, error) = await NormalizeNetBoxConfigAsync(config, previousTokenRef: null);
            if (error is not null) return error;
            config = normalized;
            if (DeniedTokenChange(previousConfig: null, config) is { } denied) return denied;
        }

        var now = DateTime.UtcNow;
        var source = new InventorySourceModel
        {
            InventorySourceId = Guid.NewGuid(),
            Name = dto.Name,
            Type = type,
            Description = dto.Description,
            Config = config,
            Mapping = dto.Mapping ?? default,
            SyncMode = dto.SyncMode ?? string.Empty,
            Enabled = dto.Enabled ?? true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _sources.Add(source);
        try
        {
            await _sources.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "inventory_source.create.failed inventory_source_name={InventorySourceName}", source.Name);
            throw;
        }

        // Config can carry credentials for the upstream IPAM/CMDB, so only
        // the non-secret shape is recorded; `config_changed` on update says a
        // connection detail moved without saying which.
        await _audit.LogAsync("inventory_source", source.InventorySourceId, "create",
            after: new { source.Name, source.Type, source.SyncMode, source.Enabled });

        _logger.LogInformation(
            "inventory_source.create.ok inventory_source_id={InventorySourceId} inventory_source_name={InventorySourceName}",
            source.InventorySourceId, source.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "InventorySource",
            routeValues: new { id = source.InventorySourceId },
            value: ToResponse(source));
    }

    public async Task<ActionResult<InventorySourceResponse>> UpdateAsync(Guid id, UpdateInventorySource dto)
    {
        var source = await _sources.GetByIdAsync(id);
        if (source is null)
        {
            _logger.LogWarning("inventory_source.update.not_found inventory_source_id={InventorySourceId}", id);
            return new NotFoundObjectResult(new { error = "inventory_source not found" });
        }

        // Snapshot before the dto lands (`source` is tracked).
        var auditBefore = new { source.Name, source.Type, source.SyncMode, source.Enabled };

        var newType = dto.Type is null ? source.Type
            : NetBoxSourceConfig.IsNetBox(dto.Type) ? NetBoxSourceConfig.Type : dto.Type;
        var newConfig = dto.Config ?? source.Config;
        if (newType == NetBoxSourceConfig.Type && (dto.Config is not null || dto.Type is not null))
        {
            var previous = NetBoxSourceConfig.IsNetBox(source.Type) ? source.Config : default(JsonElement?);
            var previousToken = previous is { } p ? NetBoxSourceConfig.Parse(p).TokenSecretRef : null;
            var (normalized, error) = await NormalizeNetBoxConfigAsync(newConfig, previousToken);
            if (error is not null) return error;
            newConfig = normalized;
            if (DeniedTokenChange(previous, newConfig) is { } denied) return denied;
        }

        if (dto.Name is not null) source.Name = dto.Name;
        source.Type = newType;
        if (dto.Description is not null) source.Description = dto.Description;
        source.Config = newConfig;
        if (dto.Mapping is not null) source.Mapping = dto.Mapping.Value;
        if (dto.SyncMode is not null) source.SyncMode = dto.SyncMode;
        if (dto.Enabled is not null) source.Enabled = dto.Enabled.Value;

        source.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _sources.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "inventory_source.update.failed inventory_source_id={InventorySourceId}", source.InventorySourceId);
            throw;
        }

        await _audit.LogAsync("inventory_source", source.InventorySourceId, "update",
            before: auditBefore,
            after: new
            {
                source.Name, source.Type, source.SyncMode, source.Enabled,
                config_changed = dto.Config is not null,
                mapping_changed = dto.Mapping is not null,
            });

        _logger.LogInformation("inventory_source.update.ok inventory_source_id={InventorySourceId}", source.InventorySourceId);
        return ToResponse(source);
    }

    public async Task<ActionResult<InventorySourceResponse>> DeleteAsync(Guid id)
    {
        var source = await _sources.GetByIdAsync(id);
        if (source is null)
        {
            _logger.LogWarning("inventory_source.delete.not_found inventory_source_id={InventorySourceId}", id);
            return new NotFoundObjectResult(new { error = "inventory_source not found" });
        }

        source.IsActive = false;
        source.UpdatedAt = DateTime.UtcNow;
        await _sources.SaveChangesAsync();

        await _audit.LogAsync("inventory_source", source.InventorySourceId, "delete",
            before: new { source.Name, source.Type, source.SyncMode });

        _logger.LogInformation("inventory_source.delete.ok inventory_source_id={InventorySourceId}", source.InventorySourceId);
        return ToResponse(source);
    }

    public async Task<ActionResult<InventorySyncResult>> SyncAsync(Guid id, bool dryRun, CancellationToken ct = default)
    {
        var source = await _sources.GetByIdAsync(id, ct: ct);
        if (source is null)
        {
            _logger.LogWarning("inventory_source.sync.not_found inventory_source_id={InventorySourceId}", id);
            return new NotFoundObjectResult(new { error = "inventory_source not found" });
        }
        if (!source.Enabled)
            return new BadRequestObjectResult(new { error = "inventory source is disabled; enable it before syncing" });

        InventorySyncResult result;
        try
        {
            result = await _netbox.SyncAsync(source, dryRun, ct);
        }
        catch (InventorySyncException ex)
        {
            _logger.LogWarning(
                "inventory_source.sync.failed inventory_source_id={InventorySourceId} dry_run={DryRun} error={Error}",
                source.InventorySourceId, dryRun, ex.Message);
            if (!dryRun)
            {
                source.LastSyncStatus = Truncate($"failed: {ex.Message}", 500);
                source.UpdatedAt = DateTime.UtcNow;
                await _sources.SaveChangesAsync(ct);
                await _audit.LogAsync("inventory_source", source.InventorySourceId, "sync",
                    after: new { status = "failed", error = Truncate(ex.Message, 300) }, ct: ct);
            }
            return new ObjectResult(new { error = ex.Message }) { StatusCode = ex.StatusCode };
        }

        if (!dryRun)
        {
            var now = DateTime.UtcNow;
            source.LastSyncAt = now;
            source.LastSyncStatus =
                $"ok: {result.Created} created, {result.Updated} updated, {result.Unchanged} unchanged, "
                + $"{result.Skipped} skipped ({result.Total} in NetBox)";
            source.UpdatedAt = now;
            await _sources.SaveChangesAsync(ct);
            await _audit.LogAsync("inventory_source", source.InventorySourceId, "sync",
                after: new { status = "ok", result.Created, result.Updated, result.Unchanged, result.Skipped, result.Total },
                ct: ct);
        }

        return result;
    }

    private async Task<(JsonElement Config, ObjectResult? Error)> NormalizeNetBoxConfigAsync(
        JsonElement config, string? previousTokenRef)
    {
        var (normalized, error) = await NetBoxSourceConfig.NormalizeAsync(
            config, previousTokenRef, _secrets, _integrations, _credentials, CancellationToken.None);
        if (error is null) return (normalized, null);
        _logger.LogWarning("inventory_source.config.validation_failed reason={Reason}", error);
        return (config, new BadRequestObjectResult(new { error }));
    }

    // inventory.manage is an Operator capability, but the sync resolves the
    // token reference server-side and sends it to base_url. An Operator who
    // could point an authenticated source at a host of their choosing — or
    // attach any stored secret to one — could read that secret off the wire.
    // So setting or changing the token reference, or moving base_url while
    // one is set, is reserved to admins; everything else stays Operator.
    private ObjectResult? DeniedTokenChange(JsonElement? previousConfig, JsonElement newConfig)
    {
        if (IsAdmin()) return null;
        var next = NetBoxSourceConfig.Parse(newConfig);
        if (next.TokenSecretRef is null) return null;
        var prev = previousConfig is { } p ? NetBoxSourceConfig.Parse(p) : null;
        var changed = prev is null
            || prev.TokenSecretRef != next.TokenSecretRef
            || !string.Equals(prev.BaseUrl.TrimEnd('/'), next.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        if (!changed) return null;
        _logger.LogWarning("inventory_source.config.token_change_denied user_id={UserId}", _caller.UserId);
        return new ObjectResult(new
        {
            error = "only an admin can set the token reference of an inventory source, or change its base_url while one is set",
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }

    private bool IsAdmin() => _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static InventorySourceResponse ToResponse(InventorySourceModel s) => new()
    {
        InventorySourceId = s.InventorySourceId,
        Name = s.Name,
        Type = s.Type,
        Description = s.Description,
        // A stored literal token is never shipped to the client; references are inert.
        Config = NetBoxSourceConfig.MaskForResponse(s.Type, s.Config),
        Mapping = s.Mapping,
        SyncMode = s.SyncMode,
        Enabled = s.Enabled,
        LastSyncAt = s.LastSyncAt,
        LastSyncStatus = s.LastSyncStatus,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
