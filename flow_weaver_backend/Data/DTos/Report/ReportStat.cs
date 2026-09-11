using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// One tile in the "stats-grid" row of a ReportDocument. `Tone` maps directly
// to the `.stat-critical|.stat-high|.stat-medium|.stat-low|.stat-ok|.stat-accent`
// variants defined in reporte.html so HTML/PDF exports match the reference
// look without extra translation logic in the exporters.
public class ReportStat
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    /// <summary>
    /// One of: critical, high, medium, low, ok, accent, neutral.
    /// Unknown tones fall back to neutral in the exporter.
    /// </summary>
    [JsonPropertyName("tone")]
    public string Tone { get; set; } = "neutral";
}
