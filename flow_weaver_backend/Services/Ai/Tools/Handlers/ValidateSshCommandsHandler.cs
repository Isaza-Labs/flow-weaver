using System.Text.Json;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Validation;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// AI tool the agent calls before locking in an ssh node's command list.
// Pass `device_type` and the candidate `commands`; the handler returns
// any commands that aren't recognised by the vendor_commands catalog
// plus "did you mean" suggestions. Warnings are advisory — the agent
// still owns the call to keep, drop, or add the command to the
// catalog.
//
// Distinct from evaluate_prompt_sufficiency (intake / required-keys
// check). This one runs once the agent has decided a draft set of
// commands; calling both is cheap because each does a different job.
public sealed class ValidateSshCommandsHandler : IToolHandler
{
    public string Name => "validate_ssh_commands";

    public string Description =>
        "Check whether each candidate SSH command is recognised in the " +
        "vendor_commands catalog for the given device_type. Returns warnings with " +
        "suggested corrections — call this BEFORE create_workflow_plan to avoid " +
        "shipping typos that only fail at execution time.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["device_type", "commands"],
          "properties": {
            "device_type": {
              "type": "string",
              "description": "Netmiko device_type (cisco_ios, juniper_junos, nokia_srl, ...)."
            },
            "commands": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 1,
              "description": "Candidate commands to validate against the catalog."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly ICurrentUser _caller;
    private readonly IVendorCommandValidator _validator;
    private readonly IVendorCommandRegistry _registry;
    private readonly ILogger<ValidateSshCommandsHandler> _logger;

    public ValidateSshCommandsHandler(
        ICurrentUser caller,
        IVendorCommandValidator validator,
        IVendorCommandRegistry registry,
        ILogger<ValidateSshCommandsHandler> logger)
    {
        _caller = caller;
        _validator = validator;
        _registry = registry;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var deviceType = GetString(args, "device_type")?.Trim() ?? string.Empty;
        var commands = GetStringList(args, "commands");

        if (string.IsNullOrWhiteSpace(deviceType))
        {
            return JsonSerializer.SerializeToElement(new
            {
                valid = false,
                warnings = new[] { "device_type is required." },
                known_commands = Array.Empty<string>(),
            });
        }
        if (commands.Count == 0)
        {
            return JsonSerializer.SerializeToElement(new
            {
                valid = false,
                warnings = new[] { "commands list must not be empty." },
                known_commands = Array.Empty<string>(),
            });
        }

        var warnings = await _validator.ValidateCommandsAsync(deviceType, commands, ct);

        // Surface up to 30 known commands so the agent can offer the user
        // a concrete picklist when its draft is wrong. More than that is
        // noise — the admin UI is the right place to browse the full
        // catalog.
        var known = await _registry.KnownCommandsForDeviceTypeAsync(deviceType, ct);
        var knownSample = known.Take(30).ToList();

        _logger.LogInformation(
            "ai.tool.validate_ssh_commands.ok device_type={DeviceType} commands={CommandCount} warnings={WarningCount}",
            deviceType, commands.Count, warnings.Count);

        return JsonSerializer.SerializeToElement(new
        {
            valid = warnings.Count == 0,
            device_type = deviceType,
            commands_checked = commands.Count,
            warnings,
            known_commands = knownSample,
            catalog_total = known.Count,
        });
    }

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> GetStringList(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
