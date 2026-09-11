using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

public class AIProvider : BaseModel
{
    public Guid AIProviderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? BaseURL { get; set; }

    // API key is stored encrypted. Use ICredentialEncryptionService to round-trip.
    [JsonIgnore]
    public byte[]? EncryptedApiKey { get; set; }

    public string DefaultModel { get; set; } = string.Empty;
    public JsonElement Config { get; set; } = default;
    public bool Enabled { get; set; }
}
