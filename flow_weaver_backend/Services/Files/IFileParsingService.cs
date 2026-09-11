namespace flow_weaver_backend.Services.Files;

// Column/row view of a parsed tabular file.
public sealed class ParsedTable
{
    public List<string> Columns { get; init; } = [];
    public List<Dictionary<string, string?>> Rows { get; init; } = [];

    /// <summary>
    /// Which sheet these rows came from, and every sheet the workbook holds.
    /// </summary>
    /// <remarks>
    /// A workbook is usually several sheets — the reports this product exports are
    /// a "Summary" plus one per section — and the parser reads one. Without saying
    /// which, "the spreadsheet has these columns" is a claim about sheet one that
    /// reads as a claim about the file, and the caller has no way to discover the
    /// rest even exists. Empty for CSV, which has no sheets.
    /// </remarks>
    public string? Sheet { get; init; }
    public List<string> SheetNames { get; init; } = [];
}

// Text view of a parsed document. PDF: one entry in Pages per page, Tables empty
// (PDF has no table markup to extract). DOCX: a single Pages entry with the body
// text (tables marked inline as [table N]) plus each table as a grid in Tables.
public sealed class ParsedDocument
{
    public List<string> Pages { get; init; } = [];
    public List<ParsedTable> Tables { get; init; } = [];
}

// Parses tabular files (CSV / XLSX) into columns + row objects and documents
// (PDF / DOCX) into text + tables. JSON and plain text are handled directly by
// the tool handler.
public interface IFileParsingService
{
    ParsedTable ParseCsv(string text, bool hasHeader);
    /// <summary>
    /// Reads one worksheet. <paramref name="sheet"/> names it; omitted, the first
    /// is read — and <see cref="ParsedTable.SheetNames"/> says what else was there.
    /// </summary>
    ParsedTable ParseXlsx(byte[] bytes, bool hasHeader, string? sheet = null);
    ParsedDocument ParsePdf(byte[] bytes);
    ParsedDocument ParseDocx(byte[] bytes, bool hasHeader);
}
