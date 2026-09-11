using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// A tabular block inside a ReportSection. Rows are flat arrays of strings
// so the same shape serializes cleanly to CSV/XLSX without a custom
// adapter. Severity highlighting lives in SeverityColumns — the exporter
// paints the cell background/foreground using ReportTheme to match the
// `.sev-badge` styles in reporte.html.
public class ReportTable
{
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("headers")]
    public List<string> Headers { get; set; } = new();

    [JsonPropertyName("rows")]
    [JsonConverter(typeof(LenientStringRowsConverter))]
    public List<List<string>> Rows { get; set; } = new();

    /// <summary>
    /// Column-index → tone mapping. Key = zero-based column index;
    /// value = critical|high|medium|low|ok (matches .sev-* in the HTML).
    /// </summary>
    [JsonPropertyName("severity_columns")]
    public Dictionary<int, string>? SeverityColumns { get; set; }
}
