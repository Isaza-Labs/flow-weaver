using System.Text.Json;
using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Utils.Report.Exporters;

// The report as data rather than as a page: stats and tables in the structure
// they already have, for a caller that is going to feed it to something else
// instead of reading it.
//
// The whole document is emitted, not just its tables. A consumer that only wants
// rows reaches into `sections[].tables[].rows`, and one that wants provenance has
// the title, who generated it and when — which is exactly what gets lost when a
// report is flattened to CSV and then has to be explained in an email.
//
// Values stay strings, as they arrive in ReportTable.Rows. Retyping them here
// would turn a leading-zero asset tag into a number, an octet into a float and a
// version "1.10" into 1.1, and the exporter has no way to know which of those the
// author meant.
public sealed class JsonReportExporter : IReportExporter
{
    public string Format => "json";
    public string ContentType => "application/json; charset=utf-8";
    public string FileExtension => "json";

    private static readonly JsonWriterOptions Options = new() { Indented = true };

    public Task<byte[]> ExportAsync(ReportDocument doc, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            w.WriteString("title", doc.Title);
            if (!string.IsNullOrWhiteSpace(doc.Subtitle)) w.WriteString("subtitle", doc.Subtitle);
            if (!string.IsNullOrWhiteSpace(doc.Badge)) w.WriteString("badge", doc.Badge);
            if (!string.IsNullOrWhiteSpace(doc.GeneratedBy)) w.WriteString("generated_by", doc.GeneratedBy);
            w.WriteString("generated_at", (doc.GeneratedAt ?? DateTime.UtcNow).ToString("u"));

            w.WriteStartArray("stats");
            foreach (var s in doc.Stats)
            {
                w.WriteStartObject();
                w.WriteString("label", s.Label);
                w.WriteString("value", s.Value);
                if (!string.IsNullOrWhiteSpace(s.Hint)) w.WriteString("hint", s.Hint);
                w.WriteString("tone", s.Tone);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("sections");
            foreach (var section in doc.Sections)
            {
                w.WriteStartObject();
                w.WriteString("title", section.Title);
                if (!string.IsNullOrWhiteSpace(section.Description)) w.WriteString("description", section.Description);
                if (!string.IsNullOrWhiteSpace(section.Category)) w.WriteString("category", section.Category);

                w.WriteStartArray("tables");
                foreach (var table in section.Tables) WriteTable(w, table);
                w.WriteEndArray();

                w.WriteStartArray("callouts");
                foreach (var callout in section.Callouts) WriteCallout(w, callout);
                w.WriteEndArray();

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Task.FromResult(stream.ToArray());
    }

    // Rows are written as objects keyed by header, not as positional arrays: a
    // consumer reading `row["device"]` keeps working when a column is added, and
    // one reading `row[2]` does not.
    //
    // A row shorter than the headers gets nulls for the rest rather than being
    // truncated, so every object carries every key and the array is rectangular.
    private static void WriteTable(Utf8JsonWriter w, ReportTable table)
    {
        w.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(table.Caption)) w.WriteString("caption", table.Caption);

        w.WriteStartArray("headers");
        foreach (var h in table.Headers) w.WriteStringValue(h);
        w.WriteEndArray();

        w.WriteStartArray("rows");
        foreach (var row in table.Rows)
        {
            w.WriteStartObject();
            for (var i = 0; i < table.Headers.Count; i++)
            {
                w.WritePropertyName(table.Headers[i]);
                if (i < row.Count && row[i] is not null) w.WriteStringValue(row[i]);
                else w.WriteNullValue();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCallout(Utf8JsonWriter w, ReportCallout callout)
    {
        w.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(callout.Title)) w.WriteString("title", callout.Title);
        w.WriteString("body", callout.Body);
        w.WriteString("tone", callout.Tone);
        w.WriteEndObject();
    }
}
