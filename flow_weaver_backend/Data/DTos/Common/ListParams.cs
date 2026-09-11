using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class ListParams
{
    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 20;

    [JsonPropertyName("offset")]
    public int Offset { get; set; } = 0;

    [JsonPropertyName("search")]
    public string? Search { get; set; }
}
