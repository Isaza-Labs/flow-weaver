using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// A highlighted box rendered at the end of a section — mirrors the
// `.callout`/`.callout.warn|danger|success` pattern from reporte.html.
public class ReportCallout
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// One of: info (default), warn, danger, success.
    /// </summary>
    [JsonPropertyName("tone")]
    public string Tone { get; set; } = "info";
}
