using System.Text;
using System.Text.RegularExpressions;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Utils.Report;

namespace flow_weaver_backend.Services.Ai.Reports;

// Resolver for `${report:<report_artifact_id>}` markers,
// mirroring SecretResolver: the REST executor passes the (already
// secret-substituted) body through SubstituteAsync right before hitting the
// wire, so the agent can reference a persisted report by id and we splice in
// the base64 server-side. Multiple markers in one `attachments[]` array =
// several formats delivered in a single email.
public sealed class ReportReferenceResolver : IReportReferenceResolver
{
    // ${report:<guid>}. The id is validated as a Guid below rather than in
    // the pattern so a malformed id produces a clear "unresolved" error
    // instead of silently not matching and shipping the literal marker.
    private static readonly Regex TokenPattern = new(
        @"\$\{report:(?<id>[^}]+)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string Marker = "${report:";

    private readonly IReportService _reports;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ReportReferenceResolver> _logger;

    public ReportReferenceResolver(
        IReportService reports,
        ICurrentUser caller,
        ILogger<ReportReferenceResolver> logger)
    {
        _reports = reports;
        _caller = caller;
        _logger = logger;
    }

    public async Task<string> SubstituteAsync(string body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body) || !body.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            return body;

        var matches = TokenPattern.Matches(body);
        if (matches.Count == 0) return body;

        // Snapshot the caller once. Attachment resolution mirrors the
        // download ACL: owner-or-admin — a non-admin can only attach reports
        // they own, so the email path never widens what the download path
        // allows.
        var userId = _caller.IsAuthenticated ? _caller.UserId : (Guid?)null;
        var isAdmin = _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

        // Cache per call so `${report:X}` appearing twice (or a filename +
        // content pair pointing at the same artifact) hits the DB once.
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        var sb = new StringBuilder(body.Length);
        var cursor = 0;

        foreach (Match m in matches)
        {
            sb.Append(body, cursor, m.Index - cursor);
            cursor = m.Index + m.Length;

            var raw = m.Groups["id"].Value.Trim();

            if (resolved.TryGetValue(raw, out var cached))
            {
                sb.Append(cached);
                continue;
            }

            var base64 = await ResolveOneAsync(raw, userId, isAdmin, ct);
            if (base64 is null)
            {
                // Keep the marker in the output for this pass; the whole
                // call fails below, so the literal never actually ships.
                if (!unresolved.Contains(raw)) unresolved.Add(raw);
                sb.Append(m.Value);
                continue;
            }

            resolved[raw] = base64;
            sb.Append(base64);
        }

        sb.Append(body, cursor, body.Length - cursor);

        if (unresolved.Count > 0)
        {
            _logger.LogWarning(
                "report.reference.unresolved ids={Ids}",
                string.Join(",", unresolved));
            throw new ReportReferenceException(unresolved);
        }

        return sb.ToString();
    }

    private async Task<string?> ResolveOneAsync(
        string rawId, Guid? userId, bool isAdmin, CancellationToken ct)
    {
        if (!Guid.TryParse(rawId, out var id) || id == Guid.Empty)
            return null;

        var bytes = await _reports.LoadContentAsync(id, userId, isAdmin, ct);
        if (bytes is null) return null;

        _logger.LogDebug(
            "report.reference.hit report_artifact_id={Id} bytes={Bytes}",
            id, bytes.Length);
        return Convert.ToBase64String(bytes);
    }
}
