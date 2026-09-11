using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class RefreshRequest
{
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;
}
