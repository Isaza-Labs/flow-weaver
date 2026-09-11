using System.Text.Json;

namespace flow_weaver_backend.Services.Validation;

// Third-pass validator. Runs after WorkflowReferenceValidator and
// surfaces non-blocking warnings for SSH commands that don't appear in
// the vendor_commands catalog. The agent (or operator) sees
// the warnings in the workflow create/update response and can fix
// typos or add the command to the catalog before running.
//
// This validator never produces blocking errors — the catalog is
// inherently incomplete for vendor extensions and custom CLIs, so the
// failure mode is "warn and let humans decide", not "deny".
public interface IVendorCommandValidator
{
    Task<WorkflowValidationResult> ValidateAsync(
        JsonElement nodes,
        IReadOnlyCollection<Guid> targetDeviceIds,
        CancellationToken ct);

    // Direct entry point for the validate_ssh_commands AI tool: skips
    // workflow-node parsing and goes straight to per-command checks.
    // Returns one warning string per offending command.
    Task<IReadOnlyList<string>> ValidateCommandsAsync(
        string deviceType,
        IReadOnlyList<string> commands,
        CancellationToken ct);
}
