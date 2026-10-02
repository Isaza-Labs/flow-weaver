using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.Mvc;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Services.VendorCommand;

// CRUD for the ssh command catalog. Mutations invalidate the
// VendorCommandRegistry cache so the validator picks up admin edits
// without waiting for the TTL.
public class VendorCommandService : IVendorCommand
{
    // Vendor-family bucket per Netmiko device_type. Mirrors the
    // VENDOR_FAMILY map in deploy/python/flow_weaver_ssh_parsers.py — kept
    // in sync by hand because the .NET side has no way to load Python
    // modules. New device_types added there should be added here too.
    private static readonly IReadOnlyDictionary<string, string> VendorFamilyByDeviceType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cisco_ios"] = "cisco",
            ["cisco_xe"] = "cisco",
            ["cisco_xr"] = "cisco",
            ["cisco_nxos"] = "cisco",
            ["cisco_asa"] = "cisco",
            ["juniper_junos"] = "juniper",
            ["arista_eos"] = "arista",
            ["nokia_sros"] = "nokia",
            ["nokia_srl"] = "nokia",
            ["huawei"] = "huawei",
            ["huawei_vrp"] = "huawei",
            ["fortinet"] = "fortinet",
            ["paloalto_panos"] = "paloalto",
            ["f5_tmsh"] = "f5",
            ["mikrotik_routeros"] = "mikrotik",
            ["linux"] = "linux",
            ["generic"] = "generic",
        };

    private readonly IVendorCommandRepository _commands;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly IVendorCommandRegistry _registry;
    private readonly ILogger<VendorCommandService> _logger;

    public VendorCommandService(
        IVendorCommandRepository commands,
        ICurrentUser caller,
        IAuditLogger audit,
        ITraceLogger trace,
        IVendorCommandRegistry registry,
        ILogger<VendorCommandService> logger)
    {
        _commands = commands;
        _caller = caller;
        _audit = audit;
        _trace = trace;
        _registry = registry;
        _logger = logger;
    }

    public Task<ActionResult<ListResponse<VendorCommandResponse>>> GetAsync(int limit = 50, int offset = 0)
        => GetAsync(deviceType: null, limit, offset);

    public async Task<ActionResult<ListResponse<VendorCommandResponse>>> GetAsync(
        string? deviceType, int limit = 100, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _commands.CountByDeviceTypeAsync(deviceType);
        var rows = await _commands.ListByDeviceTypeAsync(deviceType, limit, offset);

        return new OkObjectResult(new ListResponse<VendorCommandResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<VendorCommandResponse>> GetByIdAsync(Guid id)
    {
        var row = await _commands.GetByIdAsync(id);
        if (row is null)
            return new NotFoundObjectResult(new { error = "vendor_command not found" });
        return ToResponse(row);
    }

    public async Task<ActionResult<VendorCommandResponse>> PostAsync(CreateVendorCommand dto)
    {
        var deviceType = (dto.DeviceType ?? string.Empty).Trim();
        var kind = NormalizeKind(dto.Kind);
        var value = NormalizeValue(dto.Value ?? string.Empty, kind);
        var vendorFamily = ResolveVendorFamily(dto.VendorFamily, deviceType);

        var validation = ValidatePayload(deviceType, kind, value);
        if (validation is not null) return new BadRequestObjectResult(new { error = validation });

        // Reject duplicates loudly instead of relying on the unique index
        // throwing a 500 — the unique index is the safety net, this is the
        // friendly path.
        var dup = await _commands.ExistsDuplicateAsync(deviceType, kind, value);
        if (dup)
            return new BadRequestObjectResult(new
            {
                error = $"vendor_command already exists for device_type='{deviceType}', kind='{kind}', value='{value}'",
            });

        var now = DateTime.UtcNow;
        var row = new VendorCommandModel
        {
            VendorCommandId = Guid.NewGuid(),
            DeviceType = deviceType,
            VendorFamily = vendorFamily,
            Kind = kind,
            Value = value,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes!.Trim(),
            Source = VendorCommandModel.SourceUser,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _commands.Add(row);
        await _commands.SaveChangesAsync();
        _registry.Invalidate();

        await _audit.LogAsync("vendor_command", row.VendorCommandId, "create",
            after: new { row.DeviceType, row.Kind, row.Value });
        await _trace.EventAsync("vendor_command.create", "validation", "completed",
            metadata: new { vendor_command_id = row.VendorCommandId, row.DeviceType, row.Kind });

        _logger.LogInformation(
            "vendor_command.create.ok vendor_command_id={Id} device_type={DeviceType} kind={Kind}",
            row.VendorCommandId, row.DeviceType, row.Kind);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "VendorCommand",
            routeValues: new { id = row.VendorCommandId },
            value: ToResponse(row));
    }

    public async Task<ActionResult<VendorCommandResponse>> UpdateAsync(Guid id, UpdateVendorCommand dto)
    {
        var row = await _commands.GetByIdAsync(id);
        if (row is null)
            return new NotFoundObjectResult(new { error = "vendor_command not found" });

        // Compute the post-merge state so we can validate the final shape,
        // not just the deltas.
        var deviceType = dto.DeviceType?.Trim() ?? row.DeviceType;
        var kind = dto.Kind is null ? row.Kind : NormalizeKind(dto.Kind);
        var value = dto.Value is null
            ? row.Value
            : NormalizeValue(dto.Value, kind);

        var validation = ValidatePayload(deviceType, kind, value);
        if (validation is not null) return new BadRequestObjectResult(new { error = validation });

        // Pre-mutation snapshot (`row` is tracked) — for a vendor command the
        // old `Value` IS the interesting part: it's the CLI string that gets
        // pushed to network gear.
        var auditBefore = new { row.DeviceType, row.Kind, row.Value, row.VendorFamily, row.Source };

        if (dto.DeviceType is not null) row.DeviceType = deviceType;
        if (dto.Kind is not null) row.Kind = kind;
        if (dto.Value is not null) row.Value = value;
        if (dto.VendorFamily is not null)
            row.VendorFamily = ResolveVendorFamily(dto.VendorFamily, row.DeviceType);
        if (dto.Notes is not null)
            row.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();

        // If the admin edits a seed row, mark it as user-managed so a
        // re-seed doesn't clobber the change.
        if (string.Equals(row.Source, VendorCommandModel.SourceSeed, StringComparison.OrdinalIgnoreCase))
            row.Source = VendorCommandModel.SourceUser;

        row.UpdatedAt = DateTime.UtcNow;
        await _commands.SaveChangesAsync();
        _registry.Invalidate();

        await _audit.LogAsync("vendor_command", row.VendorCommandId, "update",
            before: auditBefore,
            after: new { row.DeviceType, row.Kind, row.Value, row.VendorFamily, row.Source });
        await _trace.EventAsync("vendor_command.update", "validation", "completed",
            metadata: new { vendor_command_id = row.VendorCommandId });

        _logger.LogInformation("vendor_command.update.ok vendor_command_id={Id}", row.VendorCommandId);
        return ToResponse(row);
    }

    public async Task<ActionResult<VendorCommandResponse>> DeleteAsync(Guid id)
    {
        var row = await _commands.GetByIdAsync(id);
        if (row is null)
            return new NotFoundObjectResult(new { error = "vendor_command not found" });

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _commands.SaveChangesAsync();
        _registry.Invalidate();

        await _audit.LogAsync("vendor_command", row.VendorCommandId, "delete",
            before: new { row.DeviceType, row.Kind, row.Value });
        await _trace.EventAsync("vendor_command.delete", "validation", "completed",
            metadata: new { vendor_command_id = row.VendorCommandId });

        _logger.LogInformation("vendor_command.delete.ok vendor_command_id={Id}", row.VendorCommandId);
        return ToResponse(row);
    }

    private static string? ValidatePayload(string deviceType, string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(deviceType))
            return "device_type is required";
        if (!string.Equals(kind, VendorCommandModel.KindExact, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, VendorCommandModel.KindPattern, StringComparison.OrdinalIgnoreCase))
            return $"kind must be '{VendorCommandModel.KindExact}' or '{VendorCommandModel.KindPattern}'";
        if (string.IsNullOrWhiteSpace(value))
            return "value is required";

        // For patterns: confirm the regex parses. Saving a broken regex
        // would silently degrade the validator at runtime; better to fail
        // here with a clear message.
        if (string.Equals(kind, VendorCommandModel.KindPattern, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = new Regex(value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                return $"pattern is not a valid regex: {ex.Message}";
            }
        }
        return null;
    }

    private static string NormalizeKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return VendorCommandModel.KindExact;
        return raw.Trim().ToLowerInvariant();
    }

    // Exact entries are stored normalised so the validator's lookup is a
    // simple set hit. Patterns are stored as-authored — the validator
    // applies its own normalisation to the candidate command before
    // matching, but we don't want to change the regex itself.
    private static string NormalizeValue(string raw, string kind)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (string.Equals(kind, VendorCommandModel.KindExact, StringComparison.OrdinalIgnoreCase))
            return CollapseWhitespace(trimmed).ToLowerInvariant();
        return trimmed;
    }

    private static string CollapseWhitespace(string s) =>
        Regex.Replace(s, @"\s+", " ");

    private static string ResolveVendorFamily(string? requested, string deviceType)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested!.Trim().ToLowerInvariant();
        return VendorFamilyByDeviceType.TryGetValue(deviceType, out var family)
            ? family
            : "generic";
    }

    private static VendorCommandResponse ToResponse(VendorCommandModel v) => new()
    {
        VendorCommandId = v.VendorCommandId,
        DeviceType = v.DeviceType,
        VendorFamily = v.VendorFamily,
        Kind = v.Kind,
        Value = v.Value,
        Notes = v.Notes,
        Source = v.Source,
        CreatedAt = v.CreatedAt,
        UpdatedAt = v.UpdatedAt,
    };
}
