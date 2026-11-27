using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Mvc;
using DeviceModel = flow_weaver_backend.Models.Device;

namespace flow_weaver_backend.Services.Device;

public class DeviceService : IDevice
{
    private readonly IDeviceRepository _devices;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<DeviceService> _logger;
    // Optional so callers that never set an environment (tests, tools) need not
    // supply it; DI always does. Without it a non-null environment id is refused.
    private readonly IPythonEnvironmentRepository? _pythonEnvironments;

    public DeviceService(
        IDeviceRepository devices, ICurrentUser caller,
        IAuditLogger audit, ILogger<DeviceService> logger,
        IPythonEnvironmentRepository? pythonEnvironments = null)
    {
        _devices = devices;
        _caller = caller;
        _audit = audit;
        _logger = logger;
        _pythonEnvironments = pythonEnvironments;
    }

    // Null when the id names an active environment (or is null / the empty
    // GUID), else the 400 message. Readiness is NOT required: the env may
    // still be building, and the SSH handler fails clearly if it is not ready
    // at run time.
    private async Task<string?> ValidatePythonEnvironmentAsync(Guid? id)
    {
        if (id is null || id == Guid.Empty) return null;
        if (_pythonEnvironments is null || !await _pythonEnvironments.ExistsAsync(id.Value))
            return $"python_environment_id {id} does not name an existing python environment";
        return null;
    }

    // Shared audit projection so create/update/delete describe a device the
    // same way and a before/after pair is directly comparable.
    private static object DeviceAudit(DeviceModel d) => new
    {
        d.DeviceName,
        d.IpAddress,
        d.Platform,
        d.Vendor,
        d.Site,
        d.Role,
        d.Status,
        d.CredentialId,
        allow_draft = d.AllowDraft,
        allow_qa = d.AllowQa,
        allow_production = d.AllowProduction,
        // Pinning state is security-relevant: clearing a pin re-opens the
        // device to any host key, so the before/after pair has to show it.
        expected_ssh_host_key_fingerprint = d.ExpectedSshHostKeyFingerprint,
        // Which interpreter (and so which paramiko) the SSH runner uses.
        python_environment_id = d.PythonEnvironmentId,
    };

    // Normalises a caller-supplied host key pin. Returns:
    //   (true,  value) → store `value` (null means "clear the pin")
    //   (false, null)  → reject; `error` explains why
    // An empty/whitespace string is an explicit "unpin" rather than an error,
    // so the UI can clear the field without a separate endpoint.
    private static bool TryResolvePin(
        string? raw, out string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = null;
            return true;
        }

        if (!SshHostKeyFingerprint.TryNormalize(raw, out var normalized, out error))
        {
            value = null;
            return false;
        }

        value = normalized;
        return true;
    }

    public async Task<ActionResult<ListResponse<DeviceResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _devices.CountAsync();
        var devices = await _devices.ListAsync(limit, offset);

        _logger.LogDebug(
            "device.list.ok total={Total} returned={Returned}",
            total, devices.Count);

        return new OkObjectResult(new ListResponse<DeviceResponse>
        {
            Data = devices.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<DeviceResponse>> GetByIdAsync(Guid id)
    {
        var device = await _devices.GetByIdAsync(id);
        if (device is null)
        {
            _logger.LogWarning("device.get.not_found device_id={DeviceId}", id);
            return new NotFoundObjectResult(new { error = "device not found" });
        }

        return ToResponse(device);
    }

    public async Task<ActionResult<DeviceResponse>> PostAsync(CreateDevice dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceName))
        {
            _logger.LogWarning("device.create.validation_failed reason=device_name_required");
            return new BadRequestObjectResult(new { error = "device_name is required" });
        }

        if (!TryResolvePin(dto.ExpectedSshHostKeyFingerprint, out var pin, out var pinError))
        {
            _logger.LogWarning(
                "device.create.validation_failed reason=host_key_fingerprint_invalid");
            return new BadRequestObjectResult(new { error = pinError });
        }

        var envError = await ValidatePythonEnvironmentAsync(dto.PythonEnvironmentId);
        if (envError is not null)
        {
            _logger.LogWarning("device.create.validation_failed reason=python_environment_invalid");
            return new BadRequestObjectResult(new { error = envError });
        }

        var now = DateTime.UtcNow;
        var device = new DeviceModel
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = dto.DeviceName,
            IpAddress = dto.IpAddress ?? string.Empty,
            Platform = dto.Platform ?? string.Empty,
            Vendor = dto.Vendor ?? string.Empty,
            OsVersion = dto.OsVersion ?? string.Empty,
            Site = dto.Site ?? string.Empty,
            Role = dto.Role ?? string.Empty,
            SourceId = dto.SourceId,
            ExternalId = string.IsNullOrWhiteSpace(dto.ExternalId) ? null : dto.ExternalId,
            Status = dto.Status ?? string.Empty,
            LastSyncAt = dto.LastSyncAt ?? default,
            Properties = dto.Properties ?? default,
            CredentialId = dto.CredentialId ?? Guid.Empty,
            AllowDraft = dto.AllowDraft ?? true,
            AllowQa = dto.AllowQa ?? false,
            AllowProduction = dto.AllowProduction ?? true,
            ExpectedSshHostKeyFingerprint = pin,
            PythonEnvironmentId = dto.PythonEnvironmentId == Guid.Empty ? null : dto.PythonEnvironmentId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _devices.Add(device);
        try
        {
            await _devices.SaveChangesAsync();
        }
        catch (ConflictException ex)
        {
            _logger.LogWarning(
                "device.create.conflict device_name={DeviceName} source_id={SourceId} external_id={ExternalId}",
                device.DeviceName, device.SourceId, device.ExternalId);
            return new ConflictObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "device.create.failed device_name={DeviceName}", device.DeviceName);
            throw;
        }

        // The Allow* trio decides which environments may target this box, so
        // a device edit is a blast-radius change, not just inventory upkeep.
        await _audit.LogAsync("device", device.DeviceId, "create",
            after: DeviceAudit(device));

        _logger.LogInformation(
            "device.create.ok device_id={DeviceId} device_name={DeviceName}",
            device.DeviceId, device.DeviceName);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Device",
            routeValues: new { id = device.DeviceId },
            value: ToResponse(device));
    }

    public async Task<ActionResult<DeviceResponse>> UpdateAsync(Guid id, UpdateDevice dto)
    {
        var device = await _devices.GetByIdAsync(id);
        if (device is null)
        {
            _logger.LogWarning("device.update.not_found device_id={DeviceId}", id);
            return new NotFoundObjectResult(new { error = "device not found" });
        }

        // Validate before mutating anything so a bad pin can't leave the row
        // half-updated.
        string? pin = null;
        if (dto.ExpectedSshHostKeyFingerprint is not null
            && !TryResolvePin(dto.ExpectedSshHostKeyFingerprint, out pin, out var pinError))
        {
            _logger.LogWarning(
                "device.update.validation_failed device_id={DeviceId} reason=host_key_fingerprint_invalid", id);
            return new BadRequestObjectResult(new { error = pinError });
        }

        var updateEnvError = await ValidatePythonEnvironmentAsync(dto.PythonEnvironmentId);
        if (updateEnvError is not null)
        {
            _logger.LogWarning(
                "device.update.validation_failed device_id={DeviceId} reason=python_environment_invalid", id);
            return new BadRequestObjectResult(new { error = updateEnvError });
        }

        // Snapshot before the dto lands (`device` is tracked).
        var auditBefore = DeviceAudit(device);

        if (dto.DeviceName is not null) device.DeviceName = dto.DeviceName;
        if (dto.IpAddress is not null) device.IpAddress = dto.IpAddress;
        if (dto.Platform is not null) device.Platform = dto.Platform;
        if (dto.Vendor is not null) device.Vendor = dto.Vendor;
        if (dto.OsVersion is not null) device.OsVersion = dto.OsVersion;
        if (dto.Site is not null) device.Site = dto.Site;
        if (dto.Role is not null) device.Role = dto.Role;
        if (dto.SourceId is not null) device.SourceId = dto.SourceId;
        if (dto.ExternalId is not null) device.ExternalId =
            string.IsNullOrWhiteSpace(dto.ExternalId) ? null : dto.ExternalId;
        if (dto.Status is not null) device.Status = dto.Status;
        if (dto.LastSyncAt is not null) device.LastSyncAt = dto.LastSyncAt.Value;
        if (dto.Properties is not null) device.Properties = dto.Properties.Value;
        if (dto.CredentialId is not null) device.CredentialId = dto.CredentialId.Value;
        if (dto.AllowDraft is not null) device.AllowDraft = dto.AllowDraft.Value;
        if (dto.AllowQa is not null) device.AllowQa = dto.AllowQa.Value;
        if (dto.AllowProduction is not null) device.AllowProduction = dto.AllowProduction.Value;
        // Distinguishes "not sent" (leave alone) from "" (explicit unpin) —
        // TryResolvePin already mapped the latter to null.
        if (dto.ExpectedSshHostKeyFingerprint is not null)
            device.ExpectedSshHostKeyFingerprint = pin;
        // Omitted keeps it; the empty GUID clears it back to system python3.
        if (dto.PythonEnvironmentId is not null)
            device.PythonEnvironmentId =
                dto.PythonEnvironmentId == Guid.Empty ? null : dto.PythonEnvironmentId;

        device.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _devices.SaveChangesAsync();
        }
        catch (ConflictException ex)
        {
            _logger.LogWarning(
                "device.update.conflict device_id={DeviceId} source_id={SourceId} external_id={ExternalId}",
                device.DeviceId, device.SourceId, device.ExternalId);
            return new ConflictObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "device.update.failed device_id={DeviceId}", device.DeviceId);
            throw;
        }

        await _audit.LogAsync("device", device.DeviceId, "update",
            before: auditBefore, after: DeviceAudit(device));

        _logger.LogInformation("device.update.ok device_id={DeviceId}", device.DeviceId);
        return ToResponse(device);
    }

    public async Task<ActionResult<DeviceResponse>> DeleteAsync(Guid id)
    {
        var device = await _devices.GetByIdAsync(id);
        if (device is null)
        {
            _logger.LogWarning("device.delete.not_found device_id={DeviceId}", id);
            return new NotFoundObjectResult(new { error = "device not found" });
        }

        device.IsActive = false;
        device.UpdatedAt = DateTime.UtcNow;
        await _devices.SaveChangesAsync();

        await _audit.LogAsync("device", device.DeviceId, "delete",
            before: DeviceAudit(device));

        _logger.LogInformation("device.delete.ok device_id={DeviceId}", device.DeviceId);
        return ToResponse(device);
    }

    private static DeviceResponse ToResponse(DeviceModel d) => new()
    {
        DeviceId = d.DeviceId,
        DeviceName = d.DeviceName,
        IpAddress = d.IpAddress,
        Platform = d.Platform,
        Vendor = d.Vendor,
        OsVersion = d.OsVersion,
        Site = d.Site,
        Role = d.Role,
        SourceId = d.SourceId,
        ExternalId = string.IsNullOrWhiteSpace(d.ExternalId) ? null : d.ExternalId,
        Status = d.Status,
        LastSyncAt = d.LastSyncAt == default ? null : d.LastSyncAt,
        Properties = d.Properties,
        CredentialId = d.CredentialId == Guid.Empty ? null : d.CredentialId,
        AllowDraft = d.AllowDraft,
        AllowQa = d.AllowQa,
        AllowProduction = d.AllowProduction,
        ExpectedSshHostKeyFingerprint = d.ExpectedSshHostKeyFingerprint,
        PythonEnvironmentId = d.PythonEnvironmentId,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}
