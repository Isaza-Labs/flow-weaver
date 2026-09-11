using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Workflow persistence with the promotion-specific lookup. Generic CRUD
// (used by WorkflowService and the other workflow services) still resolves via
// the open-generic IRepository<Workflow>; only callers that need the promotion
// query depend on this interface.
public interface IWorkflowRepository : IRepository<Workflow>
{
    // Most recently promoted copy created from `sourceWorkflowId` (the
    // PromotedFrom FK), newest by PromotedAt, or null. Backs the promotion diff.
    Task<Workflow?> GetLatestPromotedFromAsync(
        Guid sourceWorkflowId, CancellationToken ct = default);

    // Latest active workflow with this exact name in the given environment
    // (newest by Version), or null. Backs the import name-collision check.
    Task<Workflow?> FindLatestByNameInEnvironmentAsync(
        string environment, string name, CancellationToken ct = default);

    // Lightweight (id/name/environment/nodes/edges) projection of every active
    // workflow, for structural-duplicate fingerprinting on import.
    Task<IReadOnlyList<WorkflowShape>> ListActiveShapesAsync(
        CancellationToken ct = default);

    // Active workflows, optionally filtered by environment,
    // newest first (CreatedAt DESC), capped at `limit`, projected to the
    // id/name/environment/version summary. Backs list_workflows.
    Task<IReadOnlyList<WorkflowSummary>> ListSummariesAsync(
        string? environment, int limit, CancellationToken ct = default);

    // Up to `limit` active workflows with this exact name, projected to the
    // summary. Backs the evaluate_prompt_sufficiency name-collision check.
    Task<IReadOnlyList<WorkflowSummary>> FindActiveByNameAsync(
        string name, int limit, CancellationToken ct = default);
}

// id/name/environment/version projection for workflow listings.
public sealed record WorkflowSummary(Guid WorkflowId, string Name, string Environment, int Version);

// Minimal workflow shape used by the import conflict detector to fingerprint
// existing graphs without materialising full Workflow entities.
public sealed record WorkflowShape
{
    public Guid WorkflowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public JsonElement Nodes { get; init; }
    public JsonElement Edges { get; init; }
}
