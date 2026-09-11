using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class DeleteDevice
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }
}
