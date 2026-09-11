using System.Text.Json;

namespace flow_weaver_backend.Data.Repositories;

// One grant as the resolver consumes it: the capability keys it confers and the
// ABAC conditions gating them. Name/id are carried for logging/diagnostics.
public sealed record PermissionGrantRow(
    Guid PermissionGrantId,
    string Name,
    IReadOnlyList<string> Capabilities,
    JsonElement Conditions);

// Read-only data access for EffectivePermissions. Loads the active, enabled
// grants a user is a subject of; the resolver does the capability/condition
// logic. Kept as a dedicated query repository (like IPolicyEvaluatorRepository)
// so the service layer never touches AppDbContext directly.
public interface IPermissionGrantReader
{
    Task<IReadOnlyList<PermissionGrantRow>> GetActiveGrantsForSubjectAsync(
        Guid userId, CancellationToken ct = default);
}
