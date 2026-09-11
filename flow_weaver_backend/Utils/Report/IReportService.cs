using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Utils.Report;

// Orchestrates render + persistence. Both the tool handler and the REST
// controller go through this single entry point so the audit/trace trail
// is identical regardless of source.
public interface IReportService
{
    /// <summary>
    /// Renders, compresses, persists. Returns the saved entity; the
    /// caller decides whether to expose the blob inline (tool result /
    /// sync download) or just the metadata.
    /// </summary>
    /// <param name="request">Format + document. Validated here.</param>
    /// <param name="source">agent | api | workflow.</param>
    /// <param name="context">Optional origin context (conversation id,
    ///   workflow run id, prompt…). Fields unused by the current source
    ///   may be null; the service only persists what's relevant.</param>
    Task<ReportArtifact> GenerateAndPersistAsync(
        GenerateReportRequest request,
        string source,
        ReportGenerationContext context,
        CancellationToken ct);

    /// <summary>
    /// Decompresses the persisted blob to its original bytes. Used by
    /// the admin download endpoint and by the detail endpoint to
    /// rebuild the base64 payload.
    /// </summary>
    byte[] Decompress(ReportArtifact artifact);

    /// <summary>
    /// Loads a persisted artifact's original (decompressed) bytes for a
    /// caller, applying the same authorization as the download endpoints:
    /// the row must be active and — unless
    /// <paramref name="requesterIsAdmin"/> — be owned by
    /// <paramref name="requestingUserId"/>. Returns null on any miss
    /// (unknown / soft-deleted / not owned) so the caller
    /// can't distinguish "absent" from "forbidden". Backs the
    /// <c>${report:&lt;id&gt;}</c> reference the REST executor expands into
    /// outbound attachment payloads so the agent never copies the base64
    /// blob itself.
    /// </summary>
    Task<byte[]?> LoadContentAsync(
        Guid reportArtifactId,
        Guid? requestingUserId,
        bool requesterIsAdmin,
        CancellationToken ct);
}

/// <summary>
/// Everything the service needs to stamp an artifact row with origin
/// context, captured outside the neutral ReportDocument. Nullable
/// fields are legitimately optional depending on `source`.
/// </summary>
public sealed class ReportGenerationContext
{
    public Guid? UserId { get; init; }
    public string? RequestId { get; init; }

    // Agent origin
    public Guid? AgentConversationId { get; init; }
    public Guid? AgentRunId { get; init; }
    public string? AgentName { get; init; }
    public string? AgentPrompt { get; init; }

    // Workflow origin
    public Guid? WorkflowRunId { get; init; }
    public Guid? WorkflowId { get; init; }
}
