using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Utils.Report;

// Single entry point for "turn this neutral document into a saved file".
// Handles: picking the exporter, gzipping compressible formats, redacting
// + encrypting the prompt, writing the row, emitting trace + audit.
//
// Everything here is best-effort defensive — the one thing we refuse to
// do is persist a file we can't enforce retention on, so required fields
// (Title, Format) are validated up front.
public sealed class ReportService : IReportService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly IEnumerable<IReportExporter> _exporters;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ISecretRedactor _redactor;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;
    private readonly ReportOptions _options;
    private readonly ILogger<ReportService> _logger;

    private static readonly HashSet<string> AllowedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "csv", "xlsx", "pdf", "docx", "json",
    };

    public ReportService(
        AppDbContext db,
        ICurrentUser caller,
        IEnumerable<IReportExporter> exporters,
        ICredentialEncryptionService crypto,
        ISecretRedactor redactor,
        IAuditLogger audit,
        ITraceLogger trace,
        IOptions<ReportOptions> options,
        ILogger<ReportService> logger)
    {
        _db = db;
        _caller = caller;
        _exporters = exporters;
        _crypto = crypto;
        _redactor = redactor;
        _audit = audit;
        _trace = trace;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReportArtifact> GenerateAndPersistAsync(
        GenerateReportRequest request,
        string source,
        ReportGenerationContext context,
        CancellationToken ct)
    {
        ValidateRequest(request);

        var format = request.Format.ToLowerInvariant();
        var exporter = _exporters.FirstOrDefault(x =>
            string.Equals(x.Format, format, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"no exporter registered for format '{format}' — check DI wiring");

        // Stamp generated_at if the caller didn't — keeps the header
        // deterministic and makes the stored filename reflect real time.
        request.Document.GeneratedAt ??= DateTime.UtcNow;

        var rawBytes = await exporter.ExportAsync(request.Document, ct);
        if (rawBytes.Length > _options.MaxBytesPerArtifact)
        {
            throw new InvalidOperationException(
                $"report is {rawBytes.Length} bytes, exceeds limit of {_options.MaxBytesPerArtifact}. "
                + "Filter rows or generate incrementally.");
        }

        // Compress only formats where it actually helps. xlsx is already
        // zip-packed; compressing would inflate due to gzip header overhead.
        var shouldCompress = _options.CompressFormats
            .Any(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));
        var storedBytes = shouldCompress ? Gzip(rawBytes) : rawBytes;

        var sha256 = ComputeSha256(rawBytes);

        // Redact → encrypt the agent prompt if present. The redacted
        // version is what ever gets decrypted later, so operators reading
        // the audit row never see the raw token even if the cipher is
        // rotated offline.
        byte[]? promptEncrypted = null;
        var promptRedacted = false;
        if (!string.IsNullOrWhiteSpace(context.AgentPrompt))
        {
            var (redactedText, matched) = _redactor.Redact(context.AgentPrompt);
            promptRedacted = matched;
            promptEncrypted = _crypto.Encrypt(redactedText);
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(_options.RetentionDays);
        var filename = BuildFilename(request.Document.Title, exporter.FileExtension, now);

        // Prefer the caller's explicit user (workflow handlers don't have an
        // HTTP identity bound) and fall back to ICurrentUser for the
        // HTTP-driven paths (agent tool, REST controller).
        var userId = context.UserId ?? (_caller.IsAuthenticated ? _caller.UserId : (Guid?)null);

        var artifact = new ReportArtifact
        {
            ReportArtifactId = Guid.NewGuid(),
            UserId = userId,
            Title = request.Document.Title,
            Filename = filename,
            ContentType = exporter.ContentType,
            Format = exporter.Format,
            SizeBytes = rawBytes.Length,
            StoredBytes = storedBytes.Length,
            IsCompressed = shouldCompress,
            ContentBytes = storedBytes,
            Sha256 = sha256,
            ExpiresAt = expiresAt,
            Source = source,
            AgentConversationId = context.AgentConversationId,
            AgentRunId = context.AgentRunId,
            AgentName = context.AgentName,
            AgentPromptEncrypted = promptEncrypted,
            AgentPromptRedacted = promptRedacted,
            WorkflowRunId = context.WorkflowRunId,
            WorkflowId = context.WorkflowId,
            RequestId = context.RequestId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.ReportArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("report", artifact.ReportArtifactId, "generate",
            after: new
            {
                artifact.Title,
                artifact.Format,
                artifact.Source,
                size_bytes = artifact.SizeBytes,
                artifact.AgentPromptRedacted,
            },
            ct: ct);

        await _trace.EventAsync("report.generate", "admin", "completed",
            metadata: new
            {
                report_artifact_id = artifact.ReportArtifactId,
                format = artifact.Format,
                source = artifact.Source,
                size_bytes = artifact.SizeBytes,
                stored_bytes = artifact.StoredBytes,
                compressed = artifact.IsCompressed,
                prompt_redacted = artifact.AgentPromptRedacted,
            },
            ct: ct);

        _logger.LogInformation(
            "report.generated id={Id} format={Format} source={Source} size={Size} stored={Stored} compressed={Compressed}",
            artifact.ReportArtifactId, artifact.Format, artifact.Source,
            artifact.SizeBytes, artifact.StoredBytes, artifact.IsCompressed);

        return artifact;
    }

    public byte[] Decompress(ReportArtifact artifact)
    {
        if (!artifact.IsCompressed) return artifact.ContentBytes;
        return Gunzip(artifact.ContentBytes);
    }

    public async Task<byte[]?> LoadContentAsync(
        Guid reportArtifactId,
        Guid? requestingUserId,
        bool requesterIsAdmin,
        CancellationToken ct)
    {
        // Same guard the download endpoints enforce: match id + IsActive.
        // Expired-but-not-yet-reaped rows stay IsActive until the retention
        // service soft-deletes them, so an artifact the user can still
        // download is an artifact they can still attach — the two surfaces
        // agree by construction.
        var row = await _db.ReportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportArtifactId == reportArtifactId
                                      && r.IsActive, ct);
        if (row is null) return null;

        // Mirror ReportsController.DownloadOwn: non-admins may only reach
        // reports they own. Collapsing "forbidden" to null (not a distinct
        // error) keeps another user's report ids unprobeable.
        var isOwner = row.UserId.HasValue && row.UserId == requestingUserId;
        if (!requesterIsAdmin && !isOwner) return null;

        return Decompress(row);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static void ValidateRequest(GenerateReportRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Format))
            throw new InvalidOperationException("format is required");
        if (!AllowedFormats.Contains(request.Format.Trim()))
            throw new InvalidOperationException(
                $"unsupported format '{request.Format}' — allowed: html, csv, xlsx, pdf");
        if (request.Document is null)
            throw new InvalidOperationException("document is required");
        if (string.IsNullOrWhiteSpace(request.Document.Title))
            throw new InvalidOperationException("document.title is required");
        // Sanity cap — if someone slings a 100k-row table at us, fail fast.
        foreach (var section in request.Document.Sections)
        {
            foreach (var table in section.Tables)
            {
                if (table.Rows.Count > 10_000)
                    throw new InvalidOperationException(
                        $"table '{table.Caption ?? section.Title}' has {table.Rows.Count} rows; "
                        + "cap is 10000. Paginate or filter before exporting.");
            }
        }
    }

    private static byte[] Gzip(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: false))
        {
            gz.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] input)
    {
        using var src = new MemoryStream(input);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string BuildFilename(string title, string ext, DateTime now)
    {
        var slug = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) slug.Append(ch);
            else if (ch == ' ' || ch == '-' || ch == '_') slug.Append('-');
        }
        var s = slug.ToString().Trim('-');
        if (s.Length == 0) s = "report";
        if (s.Length > 60) s = s.Substring(0, 60).Trim('-');
        return $"{s}-{now:yyyyMMddHHmmss}.{ext}";
    }
}

/// <summary>
/// Bound from appsettings:Reports. Defaults match the plan we agreed on:
/// 90-day retention, 10 MB cap, gzip everything except xlsx.
/// </summary>
public sealed class ReportOptions
{
    public const string SectionName = "Reports";

    public int RetentionDays { get; set; } = 90;
    public int SoftDeleteGraceDays { get; set; } = 7;
    public int MaxBytesPerArtifact { get; set; } = 10 * 1024 * 1024;
    public List<string> CompressFormats { get; set; } = new() { "html", "csv", "pdf" };
}
