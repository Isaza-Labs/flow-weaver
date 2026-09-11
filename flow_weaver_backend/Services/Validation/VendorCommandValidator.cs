using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using DeviceModel = flow_weaver_backend.Models.Device;

namespace flow_weaver_backend.Services.Validation;

public class VendorCommandValidator : IVendorCommandValidator
{
    private const string SshSnippetType = "ssh";
    // Cap suggestions per warning so the response stays terse. Three is
    // enough to cover common typos without flooding the agent.
    private const int MaxSuggestions = 3;

    private readonly IRepository<SnippetModel> _snippets;
    private readonly IRepository<DeviceModel> _devices;
    private readonly IVendorCommandRegistry _registry;
    private readonly ILogger<VendorCommandValidator> _logger;

    public VendorCommandValidator(
        IRepository<SnippetModel> snippets,
        IRepository<DeviceModel> devices,
        IVendorCommandRegistry registry,
        ILogger<VendorCommandValidator> logger)
    {
        _snippets = snippets;
        _devices = devices;
        _registry = registry;
        _logger = logger;
    }

    public async Task<WorkflowValidationResult> ValidateAsync(
        JsonElement nodes,
        IReadOnlyCollection<Guid> targetDeviceIds,
        CancellationToken ct)
    {
        if (nodes.ValueKind != JsonValueKind.Array)
            return WorkflowValidationResult.Ok();

        // Map snippet_id → type so we can pick out only ssh nodes. Reused
        // across the iteration; a workflow with N ssh nodes still does
        // exactly one DB call.
        var snippetIds = new HashSet<Guid>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (node.TryGetProperty("snippet_id", out var sidEl)
                && sidEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(sidEl.GetString(), out var sid))
                snippetIds.Add(sid);
        }
        if (snippetIds.Count == 0) return WorkflowValidationResult.Ok();

        var snippetTypes = (await _snippets.ListByIdsAsync(snippetIds, ct: ct))
            .ToDictionary(s => s.SnippetId, s => s.Type);

        // Pre-load each target device's Platform when the node didn't
        // declare a device_type explicitly. We need a fallback resolution
        // path because target_devices is the canonical source of vendor
        // info for most workflows.
        var distinctTargetPlatforms = new List<string>();
        if (targetDeviceIds.Count > 0)
        {
            var deviceRows = await _devices.ListByIdsAsync(targetDeviceIds, activeOnly: false, ct: ct);
            distinctTargetPlatforms = deviceRows
                .Select(d => d.Platform)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var warnings = new List<string>();

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sidEl)
                || !Guid.TryParse(sidEl.GetString(), out var snippetId))
                continue;
            if (!snippetTypes.TryGetValue(snippetId, out var type)
                || !string.Equals(type, SshSnippetType, StringComparison.OrdinalIgnoreCase))
                continue;

            var nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "(unknown)"
                : "(unknown)";

            var configOverrides = node.TryGetProperty("config_overrides", out var co)
                                  && co.ValueKind == JsonValueKind.Object
                ? co
                : default;

            var commands = ExtractCommands(configOverrides);
            if (commands.Count == 0) continue;

            // Resolve device_type: explicit per-node override wins, then
            // shared platform across targets, then defer.
            var deviceTypes = ResolveDeviceTypes(configOverrides, distinctTargetPlatforms);
            if (deviceTypes.Count == 0)
            {
                warnings.Add(
                    $"node '{nodeId}': device_type unresolved — cannot validate commands at plan time. " +
                    "Set config_overrides.device_type or assign target devices with a known Platform.");
                continue;
            }
            // When targets carry a mix of platforms we can't decide which
            // catalog applies. Surface the conflict and validate against
            // every platform so the agent sees one warning per mismatch.
            if (deviceTypes.Count > 1)
            {
                warnings.Add(
                    $"node '{nodeId}': target devices span multiple platforms ({string.Join(", ", deviceTypes)}); " +
                    "validating commands against each — pin device_type in config_overrides if you want a single source of truth.");
            }

            for (var i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i];
                foreach (var dt in deviceTypes)
                {
                    var status = await _registry.IsKnownAsync(dt, cmd, ct);
                    switch (status)
                    {
                        case KnownStatus.Known:
                        case KnownStatus.Skipped:
                            continue;
                        case KnownStatus.DeviceTypeUnknown:
                            warnings.Add(
                                $"node '{nodeId}' command[{i}]: no catalog entries for device_type '{dt}'. " +
                                "Validation is deferred — add at least one vendor_command for this device_type to enable plan-time checks.");
                            continue;
                        case KnownStatus.Unknown:
                            var suggestions = await _registry.SuggestSimilarAsync(dt, cmd, MaxSuggestions, ct);
                            warnings.Add(BuildUnknownWarning(nodeId, i, dt, cmd, suggestions));
                            continue;
                    }
                }
            }
        }

        if (warnings.Count == 0) return WorkflowValidationResult.Ok();

        _logger.LogInformation(
            "validation.vendor_command.warnings count={Count}",
            warnings.Count);
        return WorkflowValidationResult.OkWithWarnings(warnings);
    }

    public async Task<IReadOnlyList<string>> ValidateCommandsAsync(
        string deviceType,
        IReadOnlyList<string> commands,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || commands.Count == 0)
            return Array.Empty<string>();

        var warnings = new List<string>();
        for (var i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            var status = await _registry.IsKnownAsync(deviceType, cmd, ct);
            switch (status)
            {
                case KnownStatus.Known:
                case KnownStatus.Skipped:
                    continue;
                case KnownStatus.DeviceTypeUnknown:
                    warnings.Add(
                        $"command[{i}] '{cmd}': no catalog entries for device_type '{deviceType}'. " +
                        "Add at least one vendor_command for this device_type to enable plan-time checks.");
                    continue;
                case KnownStatus.Unknown:
                    var suggestions = await _registry.SuggestSimilarAsync(deviceType, cmd, MaxSuggestions, ct);
                    warnings.Add(BuildUnknownWarning(
                        nodeId: null, commandIndex: i, deviceType: deviceType, command: cmd, suggestions));
                    continue;
            }
        }
        return warnings;
    }

    private static List<string> ExtractCommands(JsonElement configOverrides)
    {
        var result = new List<string>();
        if (configOverrides.ValueKind != JsonValueKind.Object) return result;

        if (configOverrides.TryGetProperty("command", out var single)
            && single.ValueKind == JsonValueKind.String)
        {
            var s = single.GetString();
            if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
        }

        if (configOverrides.TryGetProperty("commands", out var batch)
            && batch.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in batch.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
            }
        }

        return result;
    }

    private static List<string> ResolveDeviceTypes(
        JsonElement configOverrides, IReadOnlyList<string> targetPlatforms)
    {
        if (configOverrides.ValueKind == JsonValueKind.Object
            && configOverrides.TryGetProperty("device_type", out var dtEl)
            && dtEl.ValueKind == JsonValueKind.String)
        {
            var dt = dtEl.GetString();
            if (!string.IsNullOrWhiteSpace(dt))
                return new List<string> { dt!.Trim() };
        }
        return targetPlatforms.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static string BuildUnknownWarning(
        string? nodeId, int commandIndex, string deviceType, string command, IReadOnlyList<string> suggestions)
    {
        var prefix = nodeId is null
            ? $"command[{commandIndex}]"
            : $"node '{nodeId}' command[{commandIndex}]";
        var hint = suggestions.Count > 0
            ? $" Did you mean: {string.Join(", ", suggestions.Select(s => $"'{s}'"))}?"
            : string.Empty;
        return $"{prefix}: '{command}' is not a known command for device_type '{deviceType}'.{hint} " +
               "If this is a vendor extension or custom command, add it to the vendor_commands catalog.";
    }
}
