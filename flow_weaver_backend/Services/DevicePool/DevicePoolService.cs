using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using DevicePoolModel = flow_weaver_backend.Models.DevicePool;

namespace flow_weaver_backend.Services.DevicePool;

public class DevicePoolService : IDevicePool
{
    private readonly IRepository<DevicePoolModel> _pools;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<DevicePoolService> _logger;

    public DevicePoolService(
        IRepository<DevicePoolModel> pools, ICurrentUser caller,
        IAuditLogger audit, ILogger<DevicePoolService> logger)
    {
        _pools = pools;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    // Shared audit projection — FilterRules is in it on purpose: widening a
    // pool's filter silently pulls more devices into every workflow that
    // targets the pool, and that is invisible from the workflow's own history.
    private static object PoolAudit(DevicePoolModel p) => new
    {
        p.Name,
        p.Description,
        filter_rules = p.FilterRules,
        static_member_count = p.StaticMembers?.Count ?? 0,
        allow_draft = p.AllowDraft,
        allow_qa = p.AllowQa,
        allow_production = p.AllowProduction,
    };

    public async Task<ActionResult<ListResponse<DevicePoolResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _pools.CountAsync();
        var pools = await _pools.ListAsync(limit, offset);

        _logger.LogDebug(
            "device_pool.list.ok total={Total} returned={Returned}",
            total, pools.Count);

        return new OkObjectResult(new ListResponse<DevicePoolResponse>
        {
            Data = pools.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<DevicePoolResponse>> GetByIdAsync(Guid id)
    {
        var pool = await _pools.GetByIdAsync(id);
        if (pool is null)
        {
            _logger.LogWarning("device_pool.get.not_found device_pool_id={DevicePoolId}", id);
            return new NotFoundObjectResult(new { error = "device_pool not found" });
        }

        return ToResponse(pool);
    }

    public async Task<ActionResult<DevicePoolResponse>> PostAsync(CreateDevicePool dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("device_pool.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }

        var now = DateTime.UtcNow;
        var pool = new DevicePoolModel
        {
            DevicePoolId = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            FilterRules = dto.FilterRules ?? default,
            StaticMembers = dto.StaticMembers ?? new(),
            AllowDraft = dto.AllowDraft ?? true,
            AllowQa = dto.AllowQa ?? false,
            AllowProduction = dto.AllowProduction ?? true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _pools.Add(pool);
        try
        {
            await _pools.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "device_pool.create.failed device_pool_name={DevicePoolName}", pool.Name);
            throw;
        }

        await _audit.LogAsync("device_pool", pool.DevicePoolId, "create",
            after: PoolAudit(pool));

        _logger.LogInformation(
            "device_pool.create.ok device_pool_id={DevicePoolId} device_pool_name={DevicePoolName}",
            pool.DevicePoolId, pool.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "DevicePool",
            routeValues: new { id = pool.DevicePoolId },
            value: ToResponse(pool));
    }

    public async Task<ActionResult<DevicePoolResponse>> UpdateAsync(Guid id, UpdateDevicePool dto)
    {
        var pool = await _pools.GetByIdAsync(id);
        if (pool is null)
        {
            _logger.LogWarning("device_pool.update.not_found device_pool_id={DevicePoolId}", id);
            return new NotFoundObjectResult(new { error = "device_pool not found" });
        }

        // Snapshot before the dto lands (`pool` is tracked).
        var auditBefore = PoolAudit(pool);

        if (dto.Name is not null) pool.Name = dto.Name;
        if (dto.Description is not null) pool.Description = dto.Description;
        if (dto.FilterRules is not null) pool.FilterRules = dto.FilterRules.Value;
        if (dto.StaticMembers is not null) pool.StaticMembers = dto.StaticMembers;
        if (dto.AllowDraft is not null) pool.AllowDraft = dto.AllowDraft.Value;
        if (dto.AllowQa is not null) pool.AllowQa = dto.AllowQa.Value;
        if (dto.AllowProduction is not null) pool.AllowProduction = dto.AllowProduction.Value;

        pool.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _pools.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "device_pool.update.failed device_pool_id={DevicePoolId}", pool.DevicePoolId);
            throw;
        }

        await _audit.LogAsync("device_pool", pool.DevicePoolId, "update",
            before: auditBefore, after: PoolAudit(pool));

        _logger.LogInformation("device_pool.update.ok device_pool_id={DevicePoolId}", pool.DevicePoolId);
        return ToResponse(pool);
    }

    public async Task<ActionResult<DevicePoolResponse>> DeleteAsync(Guid id)
    {
        var pool = await _pools.GetByIdAsync(id);
        if (pool is null)
        {
            _logger.LogWarning("device_pool.delete.not_found device_pool_id={DevicePoolId}", id);
            return new NotFoundObjectResult(new { error = "device_pool not found" });
        }

        pool.IsActive = false;
        pool.UpdatedAt = DateTime.UtcNow;
        await _pools.SaveChangesAsync();

        await _audit.LogAsync("device_pool", pool.DevicePoolId, "delete",
            before: PoolAudit(pool));

        _logger.LogInformation("device_pool.delete.ok device_pool_id={DevicePoolId}", pool.DevicePoolId);
        return ToResponse(pool);
    }

    private static DevicePoolResponse ToResponse(DevicePoolModel p) => new()
    {
        DevicePoolId = p.DevicePoolId,
        Name = p.Name,
        Description = p.Description,
        FilterRules = p.FilterRules,
        StaticMembers = p.StaticMembers,
        AllowDraft = p.AllowDraft,
        AllowQa = p.AllowQa,
        AllowProduction = p.AllowProduction,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
