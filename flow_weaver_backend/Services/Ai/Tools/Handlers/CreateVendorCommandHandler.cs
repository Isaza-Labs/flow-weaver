using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native create_vendor_command tool. Calls IVendorCommand (VendorCommandService)
// IN-PROCESS so validation, normalization, audit and registry-invalidation run
// under the chatting user's ICurrentUser. Operator-gated in
// PermissionClassifier — the dispatcher's role gate refuses a viewer before this
// runs.
public sealed class CreateVendorCommandHandler : IToolHandler
{
    public string Name => "create_vendor_command";

    public string Description =>
        "Tier: single_confirm. Operator+ (admin or operator). Add an SSH command to the "
        + "vendor-command catalog (used by the SSH command validator). `kind`: "
        + "\"exact\" (a literal command, stored lowercased/whitespace-collapsed) or "
        + "\"pattern\" (a regex, validated at write time). `device_type` is a free string "
        + "(e.g. cisco_ios). Duplicates are rejected.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["device_type", "value"],
          "properties": {
            "device_type": { "type": "string", "description": "e.g. cisco_ios, arista_eos" },
            "value": { "type": "string", "description": "The command (exact) or regex (pattern)." },
            "kind": { "type": "string", "enum": ["exact", "pattern"], "description": "Defaults to exact." },
            "notes": { "type": "string" },
            "vendor_family": { "type": "string", "description": "Optional; auto-resolved from device_type when omitted." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly IVendorCommand _commands;
    private readonly ILogger<CreateVendorCommandHandler> _logger;

    public CreateVendorCommandHandler(IVendorCommand commands, ILogger<CreateVendorCommandHandler> logger)
    {
        _commands = commands;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var dto = new CreateVendorCommand
        {
            DeviceType = ToolArgs.Str(args, "device_type") ?? string.Empty,
            Value = ToolArgs.Str(args, "value") ?? string.Empty,
            Kind = ToolArgs.Str(args, "kind"),
            Notes = ToolArgs.Str(args, "notes"),
            VendorFamily = ToolArgs.Str(args, "vendor_family"),
        };

        ServiceOutcome<VendorCommandResponse> outcome;
        try
        {
            outcome = ToolResults.Read(await _commands.PostAsync(dto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.create_vendor_command.failed device_type={DeviceType}", dto.DeviceType);
            return JsonSerializer.SerializeToElement(new { created = false, error = $"failed: {ex.Message}" });
        }

        if (outcome.Ok)
        {
            var vc = outcome.Value!;
            _logger.LogInformation("ai.tool.create_vendor_command.ok id={Id} device_type={DeviceType} kind={Kind}",
                vc.VendorCommandId, vc.DeviceType, vc.Kind);
            return JsonSerializer.SerializeToElement(new
            {
                created = true,
                vendor_command_id = vc.VendorCommandId,
                device_type = vc.DeviceType,
                kind = vc.Kind,
                value = vc.Value,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            created = false,
            status_code = outcome.Status,
            error = outcome.Error,
            hint = ToolResults.PermissionHint(outcome.Status, "operator (or admin)"),
        });
    }
}
