using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native delete_vendor_command tool — soft-deletes a catalog entry via
// IVendorCommand in-process. Operator-gated by the dispatcher. Reversible
// (soft delete), so single_confirm rather than human_only.
public sealed class DeleteVendorCommandHandler : IToolHandler
{
    public string Name => "delete_vendor_command";

    public string Description =>
        "Tier: single_confirm. Operator+. Remove a vendor-command catalog entry by id "
        + "(soft delete). The SSH validator will warn for that command on future workflow saves.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["vendor_command_id"],
          "properties": {
            "vendor_command_id": { "type": "string", "format": "uuid" }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly IVendorCommand _commands;
    private readonly ILogger<DeleteVendorCommandHandler> _logger;

    public DeleteVendorCommandHandler(IVendorCommand commands, ILogger<DeleteVendorCommandHandler> logger)
    {
        _commands = commands;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = ToolArgs.GuidVal(args, "vendor_command_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { deleted = false, error = "vendor_command_id (uuid) is required" });

        ServiceOutcome<VendorCommandResponse> outcome;
        try
        {
            outcome = ToolResults.Read(await _commands.DeleteAsync(id.Value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.delete_vendor_command.failed id={Id}", id);
            return JsonSerializer.SerializeToElement(new { deleted = false, error = $"failed: {ex.Message}" });
        }

        if (outcome.Ok)
        {
            _logger.LogInformation("ai.tool.delete_vendor_command.ok id={Id}", id);
            return JsonSerializer.SerializeToElement(new { deleted = true, vendor_command_id = id.Value });
        }

        return JsonSerializer.SerializeToElement(new
        {
            deleted = false,
            status_code = outcome.Status,
            error = outcome.Error,
            hint = ToolResults.PermissionHint(outcome.Status, "operator (or admin)"),
        });
    }
}
