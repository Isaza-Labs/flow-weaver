using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Attach/replace one OpenAPI spec on an EXISTING integration and
// (re)materialize its actions. Mirrors the spec half of CreateIntegrationBundle
// but targets a live integration instead of creating one.
public class AttachSpecRequest
{
    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

// Attach/replace one prompt skill on an existing integration.
public class AttachSkillRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; set; }
}

// GET /api/integration/{id}/bundle — the skills + specs currently linked to
// an integration, so the UI can list, select, and edit them in place.
public class IntegrationBundleView
{
    [JsonPropertyName("skills")]
    public List<AiPromptSkillResponse> Skills { get; set; } = new();

    [JsonPropertyName("specs")]
    public List<AiApiSpecResponse> Specs { get; set; } = new();
}

public class AttachSpecResult
{
    [JsonPropertyName("spec")]
    public AiApiSpecResponse Spec { get; set; } = new();

    // Actions created or refreshed from the spec's operations.
    [JsonPropertyName("actions_upserted")]
    public int ActionsUpserted { get; set; }

    // True when the YAML couldn't be parsed for operations — the spec row is
    // still saved so the user can fix it, but no actions were materialized.
    [JsonPropertyName("spec_unparsed")]
    public bool SpecUnparsed { get; set; }
}
