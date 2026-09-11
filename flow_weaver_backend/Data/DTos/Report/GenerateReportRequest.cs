using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Payload of POST /api/reports and of the `generate_report` agent tool.
// `Format` is a closed set — the service validates it and returns 400 on
// anything else so the agent gets an immediate, actionable error.
public class GenerateReportRequest
{
    /// <summary>
    /// One of: html, csv, xlsx, pdf.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("document")]
    public ReportDocument Document { get; set; } = new();
}
