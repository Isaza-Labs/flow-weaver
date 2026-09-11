using System.Text;
using ClosedXML.Excel;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Files;
using flow_weaver_backend.Utils.Report.Exporters;

namespace flow_weaver_backend.Tests;

// This product could generate a spreadsheet, a PDF and a Word document and could
// not read any of them. `FileParsingService` is ported from Nashira so the two
// parse a file the same way rather than two ways.
//
// The fixtures are files THIS product writes, through its own exporters. Parsing a
// hand-rolled sample would prove the parser reads that sample; parsing what the
// exporters produce proves the two halves meet, which is the point of having both.
public class FileParsingTests
{
    private static readonly FileParsingService Parser = new();

    static FileParsingTests()
    {
        // Program.cs sets this once at startup and the test host never runs it, so
        // rendering a PDF here throws QuestPDF's licence notice instead of producing
        // a document. Same value the application uses.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static ReportDocument Doc() => new()
    {
        Title = "Inventory",
        GeneratedAt = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        Sections =
        [
            new ReportSection
            {
                Title = "Devices",
                Tables =
                [
                    new ReportTable
                    {
                        Headers = ["device", "ip", "status"],
                        Rows = [["core-1", "10.0.0.1", "up"], ["edge-2", "10.0.0.2", "down"]],
                    },
                ],
            },
        ],
    };

    // ── CSV ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_becomes_columns_and_rows()
    {
        var table = Parser.ParseCsv("device,ip\ncore-1,10.0.0.1\nedge-2,10.0.0.2\n", hasHeader: true);

        Assert.Equal(["device", "ip"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("core-1", table.Rows[0]["device"]);
    }

    [Fact]
    public void Csv_respects_quoting_rather_than_splitting_on_every_comma()
    {
        // The failure this avoids is silent: a quoted description containing a comma
        // shifts every column after it by one, and the table still looks like a table.
        var table = Parser.ParseCsv("device,note\ncore-1,\"rebooted, twice\"\n", hasHeader: true);

        Assert.Single(table.Rows);
        Assert.Equal("rebooted, twice", table.Rows[0]["note"]);
    }

    [Fact]
    public void Csv_without_a_header_still_yields_addressable_columns()
    {
        var table = Parser.ParseCsv("core-1,10.0.0.1\n", hasHeader: false);

        Assert.Equal(2, table.Columns.Count);
        Assert.Single(table.Rows);
    }

    // ── XLSX ────────────────────────────────────────────────────────────────

    private static byte[] Xlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "device";
        ws.Cell(1, 2).Value = "ip";
        ws.Cell(2, 1).Value = "core-1";
        ws.Cell(2, 2).Value = "10.0.0.1";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Xlsx_becomes_columns_and_rows()
    {
        var table = Parser.ParseXlsx(Xlsx(), hasHeader: true);

        Assert.Equal(["device", "ip"], table.Columns);
        Assert.Equal("core-1", table.Rows[0]["device"]);
    }

    [Fact]
    public void The_spreadsheet_this_product_writes_can_be_read_back()
    {
        // The two halves meeting. Before this, a report exported as xlsx was a file
        // the product could produce and never look at again.
        //
        // It also found the parser's real limitation. The exporter writes a
        // "Summary" sheet plus one per section, and the parser read whichever came
        // first — so this returned the metadata block and looked like a successful
        // read of an oddly-shaped table. Naming the sheet is the fix; the assertion
        // below is what caught it.
        var bytes = new ExcelReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

        var table = Parser.ParseXlsx(bytes, hasHeader: true, sheet: "Devices");

        Assert.Equal("Devices", table.Sheet);
        Assert.Contains(table.Rows, r => r.Values.Any(v => v == "core-1"));
    }

    [Fact]
    public void A_workbook_says_which_sheet_was_read_and_which_were_not()
    {
        // Without this, "the spreadsheet has these columns" is a claim about sheet
        // one that reads as a claim about the file.
        var bytes = new ExcelReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

        var table = Parser.ParseXlsx(bytes, hasHeader: true);

        Assert.NotNull(table.Sheet);
        Assert.True(table.SheetNames.Count > 1, "the exporter writes Summary plus one sheet per section");
        Assert.Contains("Devices", table.SheetNames);
    }

    [Fact]
    public void Naming_a_sheet_that_is_not_there_is_refused_rather_than_falling_back()
    {
        // Falling back to the first sheet returns a perfectly well-formed table of
        // the wrong data, which is the kind of wrong answer nobody checks.
        var bytes = new ExcelReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

        var ex = Assert.Throws<ArgumentException>(
            () => Parser.ParseXlsx(bytes, hasHeader: true, sheet: "Nope"));

        Assert.Contains("Devices", ex.Message);
    }

    // ── PDF ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_pdf_this_product_writes_can_be_read_back()
    {
        var bytes = new PdfReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

        var doc = Parser.ParsePdf(bytes);

        Assert.NotEmpty(doc.Pages);
        Assert.Contains("core-1", string.Join("\n", doc.Pages));
    }

    // ── DOCX ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_word_document_this_product_writes_can_be_read_back()
    {
        var bytes = new WordReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

        var doc = Parser.ParseDocx(bytes, hasHeader: true);

        Assert.Contains("Inventory", string.Join("\n", doc.Pages));
        Assert.NotEmpty(doc.Tables);
        Assert.Contains(doc.Tables, t => t.Rows.Any(r => r.Values.Any(v => v == "core-1")));
    }

    [Fact]
    public void A_docx_keeps_its_tables_as_grids_not_as_prose()
    {
        var bytes = new WordReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

        var doc = Parser.ParseDocx(bytes, hasHeader: true);
        var table = doc.Tables.Last();

        Assert.Contains("device", table.Columns);
        Assert.Equal("core-1", table.Rows[0]["device"]);
    }

    // ── failure ─────────────────────────────────────────────────────────────

    [Fact]
    public void Bytes_that_are_not_the_declared_format_fail_rather_than_returning_nonsense()
    {
        // The handler turns this into an error message. What matters here is that
        // the parser refuses instead of producing an empty table that reads as
        // "the file had no rows".
        var notAnXlsx = Encoding.UTF8.GetBytes("this is not a spreadsheet");

        Assert.ThrowsAny<Exception>(() => Parser.ParseXlsx(notAnXlsx, hasHeader: true));
    }
}
