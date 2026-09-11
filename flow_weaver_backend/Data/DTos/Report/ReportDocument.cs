using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Neutral report shape. Both the agent tool and the POST /api/reports
// endpoint accept this same payload. Exporters (HTML/CSV/XLSX/PDF) read
// from here without caring where the data came from.
//
// Minimum viable document: a Title plus at least one Stat or Section.
// Validation lives in ReportService so both callers see the same errors.
public class ReportDocument
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>
    /// Short badge shown above the title, e.g. "Inventory Sync · 2026-04-21".
    /// </summary>
    [JsonPropertyName("badge")]
    public string? Badge { get; set; }

    /// <summary>
    /// Human-readable source of the report — e.g. "FlowWeaver Agent" or
    /// the requesting user's name. Rendered in the header/footer.
    /// </summary>
    [JsonPropertyName("generated_by")]
    public string? GeneratedBy { get; set; }

    /// <summary>
    /// UTC. If omitted the service stamps DateTime.UtcNow.
    /// </summary>
    [JsonPropertyName("generated_at")]
    public DateTime? GeneratedAt { get; set; }

    [JsonPropertyName("stats")]
    public List<ReportStat> Stats { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<ReportSection> Sections { get; set; } = new();
}
