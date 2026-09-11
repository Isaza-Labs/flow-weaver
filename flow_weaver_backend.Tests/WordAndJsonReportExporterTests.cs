using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Utils.Report;
using flow_weaver_backend.Utils.Report.Exporters;

namespace flow_weaver_backend.Tests;

// Word and JSON were the two formats this product could not produce: the report
// pipeline stopped at html/csv/xlsx/pdf.
//
// `IReportExporter` is a plugin per format, so adding one is a new class and a DI
// line rather than a case in a switch — which is why these tests exercise the
// exporters directly and one asserts the registry has actually grown.
public class WordAndJsonReportExporterTests
{
    private static ReportDocument Doc() => new()
    {
        Title = "Inventory sync",
        Subtitle = "Nightly run",
        Badge = "Inventory · 2026-08-31",
        GeneratedBy = "FlowWeaver Agent",
        GeneratedAt = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        Stats = [new ReportStat { Label = "Devices", Value = "128", Tone = "neutral" }],
        Sections =
        [
            new ReportSection
            {
                Title = "Unreachable",
                Description = "Devices that did not answer.",
                Callouts = [new ReportCallout { Title = "Heads up", Body = "Two are new.", Tone = "warn" }],
                Tables =
                [
                    new ReportTable
                    {
                        Caption = "By site",
                        Headers = ["device", "ip", "status"],
                        Rows =
                        [
                            ["core-1", "10.0.0.1", "down"],
                            ["edge-2", "10.0.0.2"],   // short on purpose
                        ],
                    },
                ],
            },
        ],
    };

    // ── JSON ────────────────────────────────────────────────────────────────

    private static JsonElement Json()
    {
        var bytes = new JsonReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();
        return JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement.Clone();
    }

    [Fact]
    public void Json_keeps_the_provenance_not_just_the_rows()
    {
        // The thing that is lost when a report is flattened to CSV and then has to
        // be explained in an email.
        var root = Json();

        Assert.Equal("Inventory sync", root.GetProperty("title").GetString());
        Assert.Equal("FlowWeaver Agent", root.GetProperty("generated_by").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("generated_at").GetString()));
    }

    [Fact]
    public void Json_rows_are_objects_keyed_by_header()
    {
        // Keyed, not positional: a consumer reading row["device"] survives a new
        // column, and one reading row[2] does not.
        var table = Json().GetProperty("sections")[0].GetProperty("tables")[0];
        var first = table.GetProperty("rows")[0];

        Assert.Equal("core-1", first.GetProperty("device").GetString());
        Assert.Equal("down", first.GetProperty("status").GetString());
    }

    [Fact]
    public void Json_pads_a_short_row_so_the_array_stays_rectangular()
    {
        var table = Json().GetProperty("sections")[0].GetProperty("tables")[0];
        var second = table.GetProperty("rows")[1];

        Assert.Equal("edge-2", second.GetProperty("device").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("status").ValueKind);
    }

    [Fact]
    public void Json_does_not_retype_values()
    {
        var doc = new ReportDocument
        {
            Title = "t",
            Sections =
            [
                new ReportSection
                {
                    Title = "s",
                    Tables = [new ReportTable { Headers = ["tag", "version"], Rows = [["007", "1.10"]] }],
                },
            ],
        };

        var bytes = new JsonReportExporter().ExportAsync(doc, default).GetAwaiter().GetResult();
        var row = JsonDocument.Parse(Encoding.UTF8.GetString(bytes))
            .RootElement.GetProperty("sections")[0].GetProperty("tables")[0].GetProperty("rows")[0];

        Assert.Equal("007", row.GetProperty("tag").GetString());
        Assert.Equal("1.10", row.GetProperty("version").GetString());
    }

    // ── DOCX ────────────────────────────────────────────────────────────────

    private static byte[] Docx() =>
        new WordReportExporter().ExportAsync(Doc(), default).GetAwaiter().GetResult();

    [Fact]
    public void A_docx_opens_as_a_word_document()
    {
        using var stream = new MemoryStream(Docx());
        using var word = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.NotNull(word.MainDocumentPart?.Document?.Body);
    }

    [Fact]
    public void A_docx_carries_the_report_text()
    {
        using var stream = new MemoryStream(Docx());
        using var word = WordprocessingDocument.Open(stream, isEditable: false);
        var text = word.MainDocumentPart!.Document.Body!.InnerText;

        Assert.Contains("Inventory sync", text);
        Assert.Contains("Unreachable", text);
        Assert.Contains("Two are new.", text);
    }

    [Fact]
    public void A_docx_carries_tables_as_tables()
    {
        // A table flattened into a paragraph is the difference between a document
        // and a transcript — and this format exists to be edited.
        using var stream = new MemoryStream(Docx());
        using var word = WordprocessingDocument.Open(stream, isEditable: false);
        var tables = word.MainDocumentPart!.Document.Body!
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().ToList();

        // One for the stats, one for the section's table.
        Assert.Equal(2, tables.Count);
        Assert.Contains("core-1", tables[1].InnerText);
    }

    [Fact]
    public void A_docx_pads_a_short_row_so_the_grid_is_not_ragged()
    {
        using var stream = new MemoryStream(Docx());
        using var word = WordprocessingDocument.Open(stream, isEditable: false);
        var table = word.MainDocumentPart!.Document.Body!
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().Last();

        var widths = table.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>()
            .Select(r => r.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Count())
            .Distinct()
            .ToList();

        Assert.Single(widths);   // every row the same width, header included
        Assert.Equal(3, widths[0]);
    }

    // ── the registry ────────────────────────────────────────────────────────

    [Fact]
    public void Each_new_format_is_claimed_by_exactly_one_exporter()
    {
        // The service picks an exporter by matching Format, so two claiming the
        // same string would make the choice depend on registration order.
        IReportExporter[] exporters =
        [
            new HtmlReportExporter(), new CsvReportExporter(),
            new ExcelReportExporter(), new PdfReportExporter(),
            new WordReportExporter(), new JsonReportExporter(),
        ];

        var formats = exporters.Select(e => e.Format).ToList();

        Assert.Equal(formats.Count, formats.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("docx", formats);
        Assert.Contains("json", formats);
    }

    [Fact]
    public void The_new_exporters_declare_a_content_type_and_an_extension()
    {
        // Both reach the browser as a download; a missing content type turns a
        // .docx into "open with…" and a wrong one into a corrupt file.
        foreach (IReportExporter e in new IReportExporter[] { new WordReportExporter(), new JsonReportExporter() })
        {
            Assert.False(string.IsNullOrWhiteSpace(e.ContentType));
            Assert.Equal(e.Format, e.FileExtension);
            Assert.DoesNotContain(".", e.FileExtension);
        }
    }
}
