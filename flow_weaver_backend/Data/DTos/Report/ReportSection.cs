using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// A thematic block with its own heading, optional category color (mapped
// to .cat-* in reporte.html), plus tables and callouts. A report is a
// linear sequence of sections — we deliberately don't support nested
// sections because XLSX/CSV exporters can't represent that without
// fighting the format.
public class ReportSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional category tag — one of:
    /// critical, api, ai, engine, store, worker, scheduler, frontend.
    /// Drives the section header color in the HTML/PDF exporters.
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("tables")]
    public List<ReportTable> Tables { get; set; } = new();

    [JsonPropertyName("callouts")]
    public List<ReportCallout> Callouts { get; set; } = new();
}
