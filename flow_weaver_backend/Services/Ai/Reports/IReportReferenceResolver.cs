namespace flow_weaver_backend.Services.Ai.Reports;

// Expands `${report:<report_artifact_id>}` markers inside an outbound REST
// body into the referenced artifact's base64 bytes. Lets the agent attach a
// persisted report to an email (or any integration) by quoting the short id
// it already got back from `generate_report` instead of copying the
// multi-KB/MB base64 blob — which the model routinely truncates or corrupts,
// and which makes bundling several formats into one message impractical.
public interface IReportReferenceResolver
{
    /// <summary>
    /// Substitutes every <c>${report:&lt;id&gt;}</c> marker in
    /// <paramref name="body"/> for the current caller. No-ops (returns the
    /// input unchanged) when no marker is present. Resolution honours the
    /// same owner-or-admin scope as the report download endpoints, so
    /// attaching a report over email can never read one the caller could not
    /// download.
    /// </summary>
    /// <exception cref="ReportReferenceException">
    /// At least one referenced artifact could not be resolved (unknown id,
    /// not owned by a non-admin caller, expired/deleted, or malformed guid).
    /// Callers surface the message to the agent instead of shipping a broken
    /// attachment.
    /// </exception>
    Task<string> SubstituteAsync(string body, CancellationToken ct);
}

/// <summary>
/// Thrown when a <c>${report:&lt;id&gt;}</c> reference cannot be resolved.
/// Unlike an unresolved <c>${secret:...}</c> (left literal so it surfaces in
/// logs), a broken attachment reference is a hard failure: shipping the
/// literal marker as base64 would attach a corrupt file, which is worse than
/// an actionable error the agent can recover from.
/// </summary>
public sealed class ReportReferenceException : Exception
{
    public IReadOnlyList<string> UnresolvedIds { get; }

    public ReportReferenceException(IReadOnlyList<string> unresolvedIds)
        : base(BuildMessage(unresolvedIds))
    {
        UnresolvedIds = unresolvedIds;
    }

    private static string BuildMessage(IReadOnlyList<string> ids) =>
        $"report attachment reference{(ids.Count == 1 ? "" : "s")} could not be resolved: "
        + string.Join(", ", ids.Select(i => $"'{i}'"))
        + ". The report may have expired or the id is wrong. "
        + "Regenerate it with generate_report and use the fresh report_artifact_id.";
}
