using System.Text;
using System.Text.Json;
using flow_weaver_backend.Services.Files;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Utils.Report;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Parses a file into structured data. Ported from Nashira, where the same tool
// carries the same name, schema and output shapes — a user moving between the two
// products should not have to learn a second name for one thing.
//
// One source differs, and it has to. Nashira's version takes an `attachment`: the
// filename of a file the user attached to the conversation. This product has no
// user-uploaded attachments — its "attachment" is outbound, a tool result carried
// on an SSE event — so there is nothing to name.
//
// Left at `content` / `content_base64` alone, the binary formats would be
// unusable: a 5 MiB spreadsheet is ~6.7 MB of base64 for a model to reproduce
// exactly. So this product's byte source is `report_artifact_id` instead, which is
// the file source it actually has: it GENERATES xlsx, docx and pdf artifacts and
// until now could not read one back. The reference resolves through the same
// ownership check `${report:<id>}` uses, so an agent cannot read an artifact its
// caller could not download.
public sealed class ParseFileHandler : IToolHandler
{
    private const int MaxBytes = 5 * 1024 * 1024;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "format":{"type":"string","enum":["csv","xlsx","json","text","pdf","docx"],"description":"Input format (match the file extension; legacy binary .doc is not supported — ask for .docx or pdf)"},
          "report_artifact_id":{"type":"string","description":"Id of a report this instance generated (from generate_report). The way to read binary formats back."},
          "content":{"type":"string","description":"Text content (csv/json/text)"},
          "content_base64":{"type":"string","description":"Base64 content — only practical for small files; prefer report_artifact_id"},
          "sheet":{"type":"string","description":"xlsx only: which worksheet to read. Omit for the first — the result lists the others."},
          "has_header":{"type":"boolean","default":true,"description":"First row is a header (csv/xlsx, and docx tables)"},
          "page":{"type":"integer","description":"pdf only: return just this page (1-based). Omit for all pages."}
        },"required":["format"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IFileParsingService _parser;
    private readonly IReportService _reports;
    private readonly ICurrentUser _caller;

    public ParseFileHandler(IFileParsingService parser, IReportService reports, ICurrentUser caller)
    {
        _parser = parser;
        _reports = reports;
        _caller = caller;
    }

    public string Name => "parse_file";

    public string Description =>
        "Parses a file into structured data. csv/xlsx return columns + row objects; pdf returns text " +
        "per page (use `page` to fetch one); docx returns body text plus its tables as grids; json " +
        "returns the parsed value; text returns lines. For a report this instance generated, pass its " +
        "`report_artifact_id` — that is how to read xlsx, pdf and docx back, and it avoids moving a " +
        "base64 blob through the conversation. Otherwise provide `content` (csv/json/text) or, for a " +
        "small binary file, `content_base64`.";

    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var format = (Str(args, "format") ?? "").Trim().ToLowerInvariant();
        var hasHeader = Bool(args, "has_header", true);

        try
        {
            switch (format)
            {
                case "xlsx":
                {
                    var (bytes, err) = await ResolveBytesAsync(args, ct);
                    if (bytes is null) return Err(err!);
                    return Table("xlsx", Parse(bytes, hasHeader, Str(args, "sheet")));
                }
                case "csv":
                {
                    var (text, err) = await ResolveTextAsync(args, ct);
                    if (text is null) return Err(err!);
                    return Table("csv", _parser.ParseCsv(text, hasHeader));
                }
                case "json":
                {
                    var (text, err) = await ResolveTextAsync(args, ct);
                    if (text is null) return Err(err!);
                    using var doc = JsonDocument.Parse(text);
                    return JsonSerializer.SerializeToElement(new { format = "json", data = doc.RootElement.Clone() });
                }
                case "text":
                {
                    var (text, err) = await ResolveTextAsync(args, ct);
                    if (text is null) return Err(err!);
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    return JsonSerializer.SerializeToElement(new { format = "text", line_count = lines.Length, lines });
                }
                case "pdf":
                {
                    var (bytes, err) = await ResolveBytesAsync(args, ct);
                    if (bytes is null) return Err(err!);
                    var doc = _parser.ParsePdf(bytes);
                    if (Int(args, "page") is { } p)
                    {
                        if (p < 1 || p > doc.Pages.Count)
                            return Err($"page must be between 1 and {doc.Pages.Count}");
                        return JsonSerializer.SerializeToElement(
                            new { format = "pdf", page = p, page_count = doc.Pages.Count, text = doc.Pages[p - 1] });
                    }
                    return JsonSerializer.SerializeToElement(new
                    {
                        format = "pdf",
                        page_count = doc.Pages.Count,
                        pages = doc.Pages.Select((t, i) => new { page = i + 1, text = t }),
                    });
                }
                case "docx":
                {
                    var (bytes, err) = await ResolveBytesAsync(args, ct);
                    if (bytes is null) return Err(err!);
                    var doc = _parser.ParseDocx(bytes, hasHeader);
                    return JsonSerializer.SerializeToElement(new
                    {
                        format = "docx",
                        text = doc.Pages.FirstOrDefault() ?? string.Empty,
                        table_count = doc.Tables.Count,
                        tables = doc.Tables.Select(t => new { columns = t.Columns, row_count = t.Rows.Count, rows = t.Rows }),
                    });
                }
                default:
                    return Err("format must be one of csv, xlsx, json, text, pdf, docx");
            }
        }
        catch (JsonException)
        {
            return Err("content is not valid JSON");
        }
        catch (Exception ex)
        {
            return Err($"failed to parse {format}: {ex.Message}");
        }
    }

    private ParsedTable Parse(byte[] bytes, bool hasHeader, string? sheet) =>
        _parser.ParseXlsx(bytes, hasHeader, sheet);

    private static JsonElement Table(string format, ParsedTable t) =>
        JsonSerializer.SerializeToElement(
            new { format, sheet = t.Sheet, sheet_names = t.SheetNames, columns = t.Columns, row_count = t.Rows.Count, rows = t.Rows });

    private async Task<(string? Text, string? Error)> ResolveTextAsync(JsonElement args, CancellationToken ct)
    {
        var text = Str(args, "content") ?? "";
        if (text.Length == 0 && (HasProp(args, "report_artifact_id") || HasProp(args, "content_base64")))
        {
            var (bytes, err) = await ResolveBytesAsync(args, ct);
            if (bytes is null) return (null, err);
            text = Encoding.UTF8.GetString(bytes);
        }
        if (text.Length == 0) return (null, "content is required");
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return (null, "content exceeds 5 MiB");
        return (text, null);
    }

    private async Task<(byte[]? Bytes, string? Error)> ResolveBytesAsync(JsonElement args, CancellationToken ct)
    {
        // A stored artifact wins: they are the bytes this instance actually wrote,
        // with no base64 round trip for the model to mangle.
        if (Str(args, "report_artifact_id") is { Length: > 0 } rawId)
        {
            if (!Guid.TryParse(rawId.Trim(), out var id))
                return (null, "report_artifact_id is not a valid id");

            // Same ownership snapshot ReportReferenceResolver takes for
            // `${report:<id>}`: owner-or-admin, mirroring the download ACL. The
            // service deliberately does not distinguish absent from forbidden, so
            // this cannot become a way to probe for reports the caller may not read.
            var userId = _caller.IsAuthenticated ? _caller.UserId : (Guid?)null;
            var isAdmin = _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

            var content = await _reports.LoadContentAsync(id, userId, isAdmin, ct);
            if (content is null)
                return (null, $"no report artifact {id} is available to you");
            return content.Length > MaxBytes ? (null, "report exceeds 5 MiB") : (content, null);
        }

        var b64 = Str(args, "content_base64");
        if (string.IsNullOrEmpty(b64))
            return (null, "report_artifact_id or content_base64 is required");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return (null, "content_base64 is not valid base64"); }
        return bytes.Length > MaxBytes ? (null, "content exceeds 5 MiB") : (bytes, null);
    }

    private static bool HasProp(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 };
    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Err(string m) => JsonSerializer.SerializeToElement(new { error = m });
}
