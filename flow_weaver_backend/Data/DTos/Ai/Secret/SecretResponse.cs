namespace flow_weaver_backend.Dtos;

// Secret metadata safe to expose through the API. The encrypted payload
// never leaves the backend — admins rotate by writing a new value, and
// reads only ever return whether a value is set.
public class SecretResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("secret_id")]
    public Guid SecretId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }

    // True whenever the row carries ciphertext — useful for "not set yet"
    // UI states without revealing anything about the value itself.
    [System.Text.Json.Serialization.JsonPropertyName("has_value")]
    public bool HasValue { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public class CreateSecretRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public class UpdateSecretRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }

    // Null → leave ciphertext untouched (allows editing description only).
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public string? Value { get; set; }
}
