using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Services.Import.Models;

// Snapshot the user reviews before commit. Produced by the pipeline,
// consumed by the controller (`GET /import/{token}`) and the commit
// endpoint (it cross-references the user's chosen resolutions against
// the buckets here).
public sealed class AnalysisReport
{
    // "flow_weaver_v1", "n8n", "itential", "generic_dag", "unknown"
    [JsonPropertyName("format_detected")]
    public string FormatDetected { get; init; } = "unknown";

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    // Our v1 schema. Stored as JSON so the pipeline can hand it to the
    // SchemaValidator + ReferenceValidator without round-tripping types.
    [JsonPropertyName("proposed_workflow")]
    public JsonElement ProposedWorkflow { get; init; }

    // Free-form notes from the translator about decisions it made
    // (e.g. "Mapped n8n.httpRequest to rest_call; dropped 2 retry
    // settings without a v1 equivalent").
    [JsonPropertyName("translation_notes")]
    public IReadOnlyList<string> TranslationNotes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("missing_dependencies")]
    public DependenciesReport MissingDependencies { get; init; } = new();

    [JsonPropertyName("conflicts")]
    public ConflictsReport Conflicts { get; init; } = new();

    [JsonPropertyName("rollback_risk")]
    public RollbackRiskReport? RollbackRisk { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    // Present only when the upload is a bundle. Its presence is what tells the
    // commit endpoint to hand the raw body to the bundle importer instead of
    // applying the wizard's resolutions — a bundle resolves by identity and has
    // nothing to resolve interactively.
    [JsonPropertyName("bundle")]
    public BundlePlan? Bundle { get; init; }
}

// What a bundle will do to this instance, computed by the same resolution the
// importer runs rather than re-derived here. A bundle carries its dependencies,
// so nothing in it is "missing"; the question the preview answers is which of
// them this instance already has.
public sealed class BundlePlan
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    // Display only. WorkflowBundle is explicit that `exported_by` never picks a
    // code path; showing it tells the reviewer where the file came from.
    [JsonPropertyName("exported_by_product")]
    public string? ExportedByProduct { get; init; }

    [JsonPropertyName("workflow_name")]
    public string WorkflowName { get; init; } = "";

    [JsonPropertyName("node_count")]
    public int NodeCount { get; init; }

    [JsonPropertyName("edge_count")]
    public int EdgeCount { get; init; }

    // The engine's sentinels. Reported so the reviewer sees that the shape
    // survived the crossing instead of inferring it from a node count.
    [JsonPropertyName("sentinels_preserved")]
    public bool SentinelsPreserved { get; init; }

    [JsonPropertyName("snippets_to_create")]
    public IReadOnlyList<BundleSnippetPlan> SnippetsToCreate { get; init; } = Array.Empty<BundleSnippetPlan>();

    [JsonPropertyName("snippets_reused")]
    public IReadOnlyList<BundleSnippetPlan> SnippetsReused { get; init; } = Array.Empty<BundleSnippetPlan>();

    // Types the bundle declares that no handler here can execute. The import
    // still proceeds; each of these nodes needs a resolution before it can run.
    [JsonPropertyName("unrunnable_types")]
    public IReadOnlyList<BundleSnippetPlan> UnrunnableTypes { get; init; } = Array.Empty<BundleSnippetPlan>();

    // Everything the resolution degraded or left for the operator to configure.
    [JsonPropertyName("notes")]
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class BundleSnippetPlan
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    // The type the BUNDLE declares. Never inferred — that is the whole point.
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("target_mode")]
    public string? TargetMode { get; init; }

    [JsonPropertyName("runnable")]
    public bool Runnable { get; init; } = true;
}

public sealed class DependenciesReport
{
    [JsonPropertyName("snippets")]
    public IReadOnlyList<MissingSnippet> Snippets { get; init; } = Array.Empty<MissingSnippet>();

    [JsonPropertyName("integrations")]
    public IReadOnlyList<MissingIntegration> Integrations { get; init; } = Array.Empty<MissingIntegration>();

    [JsonPropertyName("vendor_commands")]
    public IReadOnlyList<MissingVendorCommand> VendorCommands { get; init; } = Array.Empty<MissingVendorCommand>();
}

public sealed class MissingSnippet
{
    // Identifier the imported document used to reference this snippet
    // (could be a name, a slug, or a foreign uuid that didn't match
    // anything in our catalogue).
    [JsonPropertyName("id_in_import")]
    public string IdInImport { get; init; } = "";

    [JsonPropertyName("inferred_type")]
    public string InferredType { get; init; } = "";

    [JsonPropertyName("actions_available")]
    public IReadOnlyList<string> ActionsAvailable { get; init; } = Array.Empty<string>();

    [JsonPropertyName("candidates_for_mapping")]
    public IReadOnlyList<MappingCandidate> CandidatesForMapping { get; init; } = Array.Empty<MappingCandidate>();

    // Phase 2: when the pipeline can call the LLM at analyze time (an
    // AI provider is configured AND we have a Snippets.md context to
    // hand it), it pre-drafts the snippet body so the wizard's
    // review step shows it as already-generated. The frontend can
    // skip the manual "Generate with AI" click and go straight to a
    // preview-and-accept flow.
    [JsonPropertyName("pregenerated_snippet")]
    public JsonElement? PregeneratedSnippet { get; init; }
}

public sealed class MissingIntegration
{
    [JsonPropertyName("id_in_import")]
    public string IdInImport { get; init; } = "";

    [JsonPropertyName("inferred_base_url")]
    public string? InferredBaseUrl { get; init; }

    [JsonPropertyName("inferred_type")]
    public string? InferredType { get; init; }

    [JsonPropertyName("actions_available")]
    public IReadOnlyList<string> ActionsAvailable { get; init; } = Array.Empty<string>();

    [JsonPropertyName("candidates_for_mapping")]
    public IReadOnlyList<MappingCandidate> CandidatesForMapping { get; init; } = Array.Empty<MappingCandidate>();
}

public sealed class MissingVendorCommand
{
    [JsonPropertyName("device_type")]
    public string DeviceType { get; init; } = "";

    [JsonPropertyName("command")]
    public string Command { get; init; } = "";
}

public sealed class MappingCandidate
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("similarity_score")]
    public double SimilarityScore { get; init; }

    // Distinguishes a snippet candidate (Id is a SnippetId) from an
    // integration candidate (Id is an IntegrationId). Drives whether
    // the commit handler routes through snippetIdRemap or through the
    // snippetToIntegrationAction materialisation path. Defaults to
    // "snippet" so older serialised payloads keep working.
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "snippet";
}

public sealed class ConflictsReport
{
    [JsonPropertyName("name_collision")]
    public NameCollision? NameCollision { get; init; }

    [JsonPropertyName("structural_duplicate")]
    public StructuralDuplicate? StructuralDuplicate { get; init; }
}

public sealed class NameCollision
{
    [JsonPropertyName("matching_workflow_id")]
    public Guid MatchingWorkflowId { get; init; }

    [JsonPropertyName("matching_workflow_environment")]
    public string MatchingWorkflowEnvironment { get; init; } = "";

    [JsonPropertyName("matching_workflow_version")]
    public int MatchingWorkflowVersion { get; init; }
}

public sealed class StructuralDuplicate
{
    [JsonPropertyName("matching_workflow_id")]
    public Guid MatchingWorkflowId { get; init; }

    [JsonPropertyName("matching_workflow_name")]
    public string MatchingWorkflowName { get; init; } = "";

    [JsonPropertyName("matching_workflow_environment")]
    public string MatchingWorkflowEnvironment { get; init; } = "";

    [JsonPropertyName("match_score")]
    public double MatchScore { get; init; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "";

    [JsonPropertyName("diff_summary")]
    public string DiffSummary { get; init; } = "";
}

public sealed class RollbackRiskReport
{
    [JsonPropertyName("non_reversible")]
    public IReadOnlyList<RollbackRiskItem> NonReversible { get; init; } = Array.Empty<RollbackRiskItem>();

    [JsonPropertyName("requires_compensation")]
    public IReadOnlyList<RollbackRiskItem> RequiresCompensation { get; init; } = Array.Empty<RollbackRiskItem>();

    [JsonPropertyName("compensated")]
    public IReadOnlyList<RollbackRiskItem> Compensated { get; init; } = Array.Empty<RollbackRiskItem>();
}

public sealed class RollbackRiskItem
{
    [JsonPropertyName("snippet_name")]
    public string SnippetName { get; init; } = "";

    [JsonPropertyName("snippet_type")]
    public string SnippetType { get; init; } = "";
}
