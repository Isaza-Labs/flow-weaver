using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Lets the agent browse the vendor_commands catalog before
// drafting an ssh node. Pairs with validate_ssh_commands:
//   - list_vendor_commands : "what's in the catalog for X?"  (explore)
//   - validate_ssh_commands: "are MY draft commands valid?" (verify)
//
// The catalog is admin-curated, so the result reflects what the admin
// has decided is known-good (baseline + edits). The agent should treat
// the list as authoritative for "did you mean" suggestions.
public sealed class ListVendorCommandsHandler : IToolHandler
{
    public string Name => "list_vendor_commands";

    public string Description =>
        "Browse the catalog of valid SSH commands per device_type. " +
        "Use this BEFORE drafting an ssh node when you don't know what commands " +
        "are catalogued for the target vendor. Returns exact entries (literal " +
        "commands) and pattern entries (regex) separately, plus the vendor_family " +
        "and source (seed|user) so the agent knows which entries the admin " +
        "explicitly added.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["device_type"],
          "properties": {
            "device_type": {
              "type": "string",
              "description": "Netmiko device_type (cisco_ios, juniper_junos, nokia_srl, nokia_sros, arista_eos, cisco_xr, cisco_nxos, cisco_xe, huawei, fortinet, linux)."
            },
            "kind": {
              "type": "string",
              "enum": ["exact", "pattern", "all"],
              "description": "Filter by entry kind. Defaults to 'all'. Use 'exact' to get a literal command list (best for picking commands the user can copy-paste); 'pattern' to see the regex coverage."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 500,
              "description": "Max entries to return. Defaults to 200; cap is 500."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IVendorCommandRepository _vendorCommands;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ListVendorCommandsHandler> _logger;

    public ListVendorCommandsHandler(
        IVendorCommandRepository vendorCommands,
        ICurrentUser caller,
        ILogger<ListVendorCommandsHandler> logger)
    {
        _vendorCommands = vendorCommands;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var deviceType = GetString(args, "device_type")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            return JsonSerializer.SerializeToElement(new
            {
                error = "device_type is required",
            });
        }

        var kindFilter = (GetString(args, "kind") ?? "all").Trim().ToLowerInvariant();
        if (kindFilter != "exact" && kindFilter != "pattern" && kindFilter != "all")
            kindFilter = "all";

        var limit = 200;
        if (args.TryGetProperty("limit", out var limEl)
            && limEl.ValueKind == JsonValueKind.Number
            && limEl.TryGetInt32(out var limParsed))
        {
            limit = Math.Clamp(limParsed, 1, 500);
        }

        var rows = await _vendorCommands.ListByDeviceTypeAndKindAsync(deviceType, kindFilter == "all" ? null : kindFilter, limit, ct);

        // Total count is informational for the agent — when total > limit
        // the agent knows the listing is truncated and can re-query with
        // a tighter `kind` filter or guide the user accordingly.
        var totalActive = await _vendorCommands.CountActiveByDeviceTypeAsync(deviceType, ct);

        var exact = rows.Where(r => string.Equals(r.Kind, VendorCommandModel.KindExact, StringComparison.OrdinalIgnoreCase))
            .Select(r => new { value = r.Value, source = r.Source, notes = r.Notes })
            .ToList();
        var patterns = rows.Where(r => string.Equals(r.Kind, VendorCommandModel.KindPattern, StringComparison.OrdinalIgnoreCase))
            .Select(r => new { value = r.Value, source = r.Source, notes = r.Notes })
            .ToList();

        var vendorFamily = rows.FirstOrDefault()?.VendorFamily ?? string.Empty;

        _logger.LogInformation(
            "ai.tool.list_vendor_commands.ok device_type={DeviceType} kind_filter={Kind} returned={Returned} total={Total}",
            deviceType, kindFilter, rows.Count, totalActive);

        return JsonSerializer.SerializeToElement(new
        {
            device_type = deviceType,
            vendor_family = vendorFamily,
            kind_filter = kindFilter,
            limit,
            returned = rows.Count,
            total_active = totalActive,
            truncated = rows.Count < totalActive,
            exact,
            patterns,
        });
    }

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
