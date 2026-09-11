using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Import;

// Flags two kinds of conflicts before commit:
//   1. Name collision: a workflow with the same `name` already exists
//      in the target environment (default `draft`).
//   2. Structural duplicate: the proposed graph fingerprint matches an
//      existing workflow's, suggesting the user is re-importing
//      something they already have.
public sealed class ConflictDetector
{
    private readonly IWorkflowRepository _workflows;
    private readonly ICurrentUser _caller;

    public ConflictDetector(IWorkflowRepository workflows, ICurrentUser caller)
    {
        _workflows = workflows;
        _caller = caller;
    }

    public async Task<ConflictsReport> DetectAsync(
        JsonElement workflow,
        string targetEnvironment,
        CancellationToken ct)
    {
        var name = workflow.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? ""
            : "";

        NameCollision? nameCollision = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var existing = await _workflows.FindLatestByNameInEnvironmentAsync(targetEnvironment, name, ct);
            if (existing is not null)
            {
                nameCollision = new NameCollision
                {
                    MatchingWorkflowId = existing.WorkflowId,
                    MatchingWorkflowEnvironment = existing.Environment,
                    MatchingWorkflowVersion = existing.Version,
                };
            }
        }

        var fingerprint = DuplicateFingerprint.Compute(workflow);
        StructuralDuplicate? duplicate = null;

        // Compare fingerprint against every active workflow. This is N
        // comparisons but each is a string hash check; with ~hundreds of
        // workflows it's still cheap.
        var candidates = await _workflows.ListActiveShapesAsync(ct);

        foreach (var c in candidates)
        {
            var existingShape = JsonDocument.Parse($$"""
                { "nodes": {{c.Nodes.GetRawText()}}, "edges": {{c.Edges.GetRawText()}} }
                """).RootElement;
            var existingFp = DuplicateFingerprint.Compute(existingShape);
            var score = DuplicateFingerprint.Similarity(fingerprint, existingFp);
            if (score >= 0.95)
            {
                duplicate = new StructuralDuplicate
                {
                    MatchingWorkflowId = c.WorkflowId,
                    MatchingWorkflowName = c.Name,
                    MatchingWorkflowEnvironment = c.Environment,
                    MatchScore = score,
                    Fingerprint = fingerprint,
                    DiffSummary = score == 1.0
                        ? "Identical DAG topology and configuration."
                        : "Same DAG topology; minor differences in node configuration.",
                };
                break;
            }
        }

        return new ConflictsReport
        {
            NameCollision = nameCollision,
            StructuralDuplicate = duplicate,
        };
    }
}
