using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native update_vendor_command tool — PATCH-style edit of a catalog entry via
// IVendorCommand in-process. Operator-gated by the dispatcher.
public sealed class UpdateVendorCommandHandler : IToolHandler
{
    public string Name => "update_vendor_command";

    public string Description =>
        "Tier: single_confirm. Operator+. Edit a vendor-command catalog entry by id. Only the "
        + "fields you pass are changed. Editing a shipped (seed) entry promotes it to a user entry.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["vendor_command_id"],
          "properties": {
            "vendor_command_id": { "type": "string", "format": "uuid" },
            "device_type": { "type": "string" },
            "value": { "type": "string" },
            "kind": { "type": "string", "enum": ["exact", "pattern"] },
            "notes": { "type": "string" },
            "vendor_family": { "type": "string" }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly IVendorCommand _commands;
    private readonly ILogger<UpdateVendorCommandHandler> _logger;

    public UpdateVendorCommandHandler(IVendorCommand commands, ILogger<UpdateVendorCommandHandler> logger)
    {
        _commands = commands;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = ToolArgs.GuidVal(args, "vendor_command_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { updated = false, error = "vendor_command_id (uuid) is required" });

        var dto = new UpdateVendorCommand
        {
            DeviceType = ToolArgs.Str(args, "device_type"),
            Value = ToolArgs.Str(args, "value"),
            Kind = ToolArgs.Str(args, "kind"),
            Notes = ToolArgs.Str(args, "notes"),
            VendorFamily = ToolArgs.Str(args, "vendor_family"),
        };

        ServiceOutcome<VendorCommandResponse> outcome;
        try
        {
            outcome = ToolResults.Read(await _commands.UpdateAsync(id.Value, dto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.update_vendor_command.failed id={Id}", id);
            return JsonSerializer.SerializeToElement(new { updated = false, error = $"failed: {ex.Message}" });
        }

        if (outcome.Ok)
        {
            var vc = outcome.Value!;
            _logger.LogInformation("ai.tool.update_vendor_command.ok id={Id}", vc.VendorCommandId);
            return JsonSerializer.SerializeToElement(new
            {
                updated = true,
                vendor_command_id = vc.VendorCommandId,
                device_type = vc.DeviceType,
                kind = vc.Kind,
                value = vc.Value,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            updated = false,
            status_code = outcome.Status,
            error = outcome.Error,
            hint = ToolResults.PermissionHint(outcome.Status, "operator (or admin)"),
        });
    }
}
