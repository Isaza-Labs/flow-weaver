using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace flow_weaver_backend.Services.Files;

public sealed class FileParsingService : IFileParsingService
{
    public ParsedTable ParseCsv(string text, bool hasHeader)
    {
        var records = ParseCsvRecords(text ?? string.Empty);
        return ToTable(records, hasHeader);
    }

    public ParsedTable ParseXlsx(byte[] bytes, bool hasHeader, string? sheet = null)
    {
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var names = wb.Worksheets.Select(w => w.Name).ToList();

        // Named sheet, else the first. Naming one that is not there is refused
        // rather than silently falling back: reading the wrong sheet returns a
        // perfectly well-formed table of the wrong data, which is the kind of
        // wrong answer nobody checks.
        var ws = sheet is { Length: > 0 }
            ? wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, sheet, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException(
                  $"no sheet named '{sheet}' — this workbook has: {string.Join(", ", names)}")
            : wb.Worksheets.FirstOrDefault();

        var range = ws?.RangeUsed();
        if (ws is null || range is null) return new ParsedTable { SheetNames = names, Sheet = ws?.Name };

        var first = range.RangeAddress.FirstAddress;
        var last = range.RangeAddress.LastAddress;
        var records = new List<List<string>>();
        for (var r = first.RowNumber; r <= last.RowNumber; r++)
        {
            var record = new List<string>();
            for (var c = first.ColumnNumber; c <= last.ColumnNumber; c++)
                record.Add(ws.Cell(r, c).GetString());
            records.Add(record);
        }
        var table = ToTable(records, hasHeader);
        return new ParsedTable
        {
            Columns = table.Columns,
            Rows = table.Rows,
            Sheet = ws.Name,
            SheetNames = names,
        };
    }

    public ParsedDocument ParsePdf(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var pages = new List<string>();
        // ContentOrderTextExtractor reconstructs reading order; Page.Text is raw
        // content-stream order, which interleaves columns and scrambles tables.
        foreach (var page in doc.GetPages())
            pages.Add(ContentOrderTextExtractor.GetText(page));
        return new ParsedDocument { Pages = pages };
    }

    public ParsedDocument ParseDocx(byte[] bytes, bool hasHeader)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return new ParsedDocument();

        // Walk top-level elements in document order so the [table N] markers land
        // where the table actually sits in the prose.
        var text = new StringBuilder();
        var tables = new List<ParsedTable>();
        foreach (var el in body.ChildElements)
        {
            switch (el)
            {
                case W.Paragraph p:
                    var line = p.InnerText;
                    if (!string.IsNullOrWhiteSpace(line)) text.AppendLine(line.Trim());
                    break;
                case W.Table t:
                    var records = t.Elements<W.TableRow>()
                        .Select(r => r.Elements<W.TableCell>().Select(c => c.InnerText.Trim()).ToList())
                        .ToList();
                    tables.Add(ToTable(records, hasHeader));
                    text.AppendLine($"[table {tables.Count}]");
                    break;
            }
        }
        return new ParsedDocument { Pages = [text.ToString()], Tables = tables };
    }

    private static ParsedTable ToTable(List<List<string>> records, bool hasHeader)
    {
        if (records.Count == 0) return new ParsedTable();

        var width = records.Max(r => r.Count);
        List<string> columns;
        int dataStart;
        if (hasHeader)
        {
            columns = Enumerable.Range(0, width)
                .Select(i => i < records[0].Count && !string.IsNullOrWhiteSpace(records[0][i]) ? records[0][i] : $"col{i + 1}")
                .ToList();
            dataStart = 1;
        }
        else
        {
            columns = Enumerable.Range(0, width).Select(i => $"col{i + 1}").ToList();
            dataStart = 0;
        }

        var table = new ParsedTable { Columns = columns };
        for (var r = dataStart; r < records.Count; r++)
        {
            var record = records[r];
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
                row[columns[i]] = i < record.Count ? record[i] : null;
            table.Rows.Add(row);
        }
        return table;
    }

    // RFC 4180-style CSV: quoted fields may contain commas, newlines, and escaped
    // ("") quotes. Handles \r\n and bare \n line endings.
    internal static List<List<string>> ParseCsvRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var sawAny = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; sawAny = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); sawAny = true; break;
                    case '\r': break;
                    case '\n':
                        record.Add(field.ToString()); field.Clear();
                        records.Add(record); record = [];
                        sawAny = false;
                        break;
                    default: field.Append(ch); sawAny = true; break;
                }
            }
        }
        if (sawAny || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }
}
