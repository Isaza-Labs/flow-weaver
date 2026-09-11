using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

public sealed class QueryDevicesHandler : IToolHandler
{
    public string Name => "query_devices";
    public string Description => "Query devices by platform, vendor, site, or role. Returns id, name, ip, platform, vendor, status, os_version, role, site, and model (when the inventory populates it in custom properties). Use os_version, role and model to choose version- and role-appropriate CLI commands for the device.";
    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{"platform":{"type":"string"},"vendor":{"type":"string"},"site":{"type":"string"},"role":{"type":"string"},"limit":{"type":"integer","default":50}},"additionalProperties":false}
        """).RootElement;

    private readonly IDeviceRepository _devices;
    private readonly ICurrentUser _caller;
    private readonly ILogger<QueryDevicesHandler> _logger;

    public QueryDevicesHandler(IDeviceRepository devices, ICurrentUser caller, ILogger<QueryDevicesHandler> logger)
    {
        _devices = devices;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var platform = args.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var vendor = args.TryGetProperty("vendor", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var site = args.TryGetProperty("site", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var role = args.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : 50;

        _logger.LogDebug(
            "ai.tool.query_devices.start platform={Platform} vendor={Vendor} site={Site} role={Role} limit={Limit}",
            platform, vendor, site, role, limit);

        try
        {
            var rows = await _devices.QueryActiveAsync(platform, vendor, site, role, Math.Clamp(limit, 1, 200), ct);

            // os_version / role / site are first-class columns; `model`
            // lives in the device's custom Properties blob (NetBox custom
            // fields, etc.), so pull it out in memory. The agent uses these
            // to pick version- and model-specific commands; null when the
            // inventory doesn't carry the field.
            var list = rows.Select(d => new
            {
                d.DeviceId, d.DeviceName, d.IpAddress, d.Platform, d.Vendor, d.Status,
                d.OsVersion, d.Role, d.Site,
                Model = ExtractModel(d.Properties),
            }).ToList();

            _logger.LogInformation(
                "ai.tool.query_devices.ok count={Count} platform={Platform} vendor={Vendor}",
                list.Count, platform, vendor);
            return JsonSerializer.SerializeToElement(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.query_devices.failed platform={Platform}", platform);
            throw;
        }
    }

    // Keys an inventory commonly uses for the hardware model, in
    // priority order. Matched case-insensitively against the device's
    // custom Properties blob — different sources (NetBox, manual import)
    // spell it differently.
    private static readonly string[] ModelKeys = { "model", "device_model", "part_number", "hardware" };

    private static string? ExtractModel(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in ModelKeys)
        {
            foreach (var prop in properties.EnumerateObject())
            {
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
        }
        return null;
    }
}
