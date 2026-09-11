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

    public InventorySourceService(
        IRepository<InventorySourceModel> sources,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<InventorySourceService> logger)
    {
        _sources = sources;
        _caller = caller;
        _audit = audit;
        _logger = logger;
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

        var now = DateTime.UtcNow;
        var source = new InventorySourceModel
        {
            InventorySourceId = Guid.NewGuid(),
            Name = dto.Name,
            Type = dto.Type,
            Description = dto.Description,
            Config = dto.Config ?? default,
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

        if (dto.Name is not null) source.Name = dto.Name;
        if (dto.Type is not null) source.Type = dto.Type;
        if (dto.Description is not null) source.Description = dto.Description;
        if (dto.Config is not null) source.Config = dto.Config.Value;
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

    private static InventorySourceResponse ToResponse(InventorySourceModel s) => new()
    {
        InventorySourceId = s.InventorySourceId,
        Name = s.Name,
        Type = s.Type,
        Description = s.Description,
        Config = s.Config,
        Mapping = s.Mapping,
        SyncMode = s.SyncMode,
        Enabled = s.Enabled,
        LastSyncAt = s.LastSyncAt,
        LastSyncStatus = s.LastSyncStatus,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
