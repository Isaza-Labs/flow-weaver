using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Utils.Report.Exporters;

// The report as a Word document — the format for a report someone is going to
// keep working on. A PDF is finished; a .docx is handed over.
//
// Styles are written into the file rather than pulled from a template. A .docx
// with no styles part renders in Word's defaults, which are not ReportTheme's,
// and shipping a template would be a second place for the palette to live.
//
// Colours come from ReportTheme with the '#' stripped, because OpenXML wants
// RRGGBB. Deriving rather than repeating means the day the palette changes, the
// Word output moves with the HTML and the PDF.
public sealed class WordReportExporter : IReportExporter
{
    public string Format => "docx";

    public string ContentType =>
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public string FileExtension => "docx";

    private static string Hex(string css) => css.TrimStart('#').ToUpperInvariant();

    public Task<byte[]> ExportAsync(ReportDocument doc, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;
            main.AddNewPart<StyleDefinitionsPart>().Styles = BuildStyles();

            if (!string.IsNullOrWhiteSpace(doc.Badge)) body.AppendChild(Muted(doc.Badge!));
            body.AppendChild(Heading(doc.Title, 1));
            if (!string.IsNullOrWhiteSpace(doc.Subtitle)) body.AppendChild(Para(doc.Subtitle!));

            var by = string.IsNullOrWhiteSpace(doc.GeneratedBy) ? "" : $"{doc.GeneratedBy} · ";
            body.AppendChild(Muted($"{by}{(doc.GeneratedAt ?? DateTime.UtcNow):u}"));

            // Stats as a two-column table. The tone and hint are presentation
            // hints for HTML and PDF; a Word document the reader will edit is
            // better off with the numbers than with a colour they cannot act on.
            if (doc.Stats.Count > 0)
            {
                body.AppendChild(Heading("Summary", 2));
                var rows = doc.Stats.Select(s => new List<string> { s.Label, s.Value }).ToList();
                body.AppendChild(Grid(["Metric", "Value"], rows));
                body.AppendChild(new Paragraph());
            }

            foreach (var section in doc.Sections)
            {
                body.AppendChild(Heading(section.Title, 2));
                if (!string.IsNullOrWhiteSpace(section.Description)) body.AppendChild(Para(section.Description!));

                foreach (var callout in section.Callouts)
                {
                    if (!string.IsNullOrWhiteSpace(callout.Title))
                        body.AppendChild(Heading(callout.Title, 3));
                    body.AppendChild(Para(callout.Body, indentTwips: 360, italic: true));
                }

                foreach (var table in section.Tables)
                {
                    if (!string.IsNullOrWhiteSpace(table.Caption)) body.AppendChild(Muted(table.Caption!));
                    body.AppendChild(Grid(table.Headers, table.Rows));
                    body.AppendChild(new Paragraph());
                }
            }

            // Word treats a section without explicit properties as legacy layout;
            // stating them keeps the page predictable across viewers.
            body.AppendChild(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134 }));

            main.Document.Save();
        }

        return Task.FromResult(stream.ToArray());
    }

    private static Paragraph Heading(string text, int level) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{Math.Clamp(level, 1, 6)}" }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Muted(string text) =>
        new(new Run(
            new RunProperties(new Color { Val = Hex(ReportTheme.TextMuted) }, new FontSize { Val = "18" }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Para(string text, int indentTwips = 0, bool italic = false)
    {
        var para = new Paragraph();
        if (indentTwips > 0)
            para.AppendChild(new ParagraphProperties(new Indentation { Left = indentTwips.ToString() }));
        var props = new RunProperties();
        if (italic) props.AppendChild(new Italic());
        para.AppendChild(new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return para;
    }

    // A row shorter than the headers is padded rather than truncated, so the grid
    // stays rectangular — Word renders a ragged table as a broken one.
    private static Table Grid(IReadOnlyList<string> headers, IReadOnlyList<List<string>> rows)
    {
        var border = Hex(ReportTheme.Border);
        var table = new Table(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = border })));

        if (headers.Count > 0)
        {
            var head = new TableRow();
            foreach (var h in headers) head.AppendChild(Cell(h, header: true));
            table.AppendChild(head);
        }

        foreach (var row in rows)
        {
            var tr = new TableRow();
            for (var i = 0; i < headers.Count; i++)
                tr.AppendChild(Cell(i < row.Count ? row[i] ?? string.Empty : string.Empty, header: false));
            table.AppendChild(tr);
        }

        return table;
    }

    private static TableCell Cell(string text, bool header)
    {
        var runProps = new RunProperties();
        if (header) runProps.AppendChild(new Bold());

        var cellProps = new TableCellProperties(new TableCellMargin(
            new LeftMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
            new RightMargin { Width = "80", Type = TableWidthUnitValues.Dxa }));
        if (header)
            cellProps.AppendChild(new Shading
            {
                Fill = Hex(ReportTheme.SurfaceAlt),
                Val = ShadingPatternValues.Clear,
            });

        return new TableCell(cellProps,
            new Paragraph(new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static Styles BuildStyles()
    {
        var styles = new Styles();
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new StyleRunProperties(
                    new Bold(),
                    new Color { Val = Hex(ReportTheme.Text) },
                    new FontSize { Val = (32 - level * 3).ToString() }))
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{level}",
            });
        }
        return styles;
    }
}
