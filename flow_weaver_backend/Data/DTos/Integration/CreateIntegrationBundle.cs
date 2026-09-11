using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Payload for POST /api/integration/bundle — a single transactional create
// that provisions the Integration plus any skills/specs the caller wants
// scoped to it. Empty lists are valid (create an integration without
// content and upload later via /ai/skills or /ai/specs).
public class CreateIntegrationBundle
{
    [JsonPropertyName("integration")]
    public CreateIntegration Integration { get; set; } = new();

    [JsonPropertyName("skills")]
    public List<BundledSkill> Skills { get; set; } = new();

    [JsonPropertyName("specs")]
    public List<BundledSpec> Specs { get; set; } = new();
}

// A skill to create alongside the integration. Shape matches the
// CreateAiPromptSkillBody the AiPromptSkillController accepts — kept
// inline here so the bundle endpoint has its own stable schema.
public class BundledSkill
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; set; }
}

// A spec to create alongside the integration. Matches CreateApiSpecBody.
public class BundledSpec
{
    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

// Response summarizing what the bundle produced.
public class CreateIntegrationBundleResult
{
    [JsonPropertyName("integration")]
    public IntegrationResponse Integration { get; set; } = new();

    [JsonPropertyName("skills_created")]
    public int SkillsCreated { get; set; }

    [JsonPropertyName("specs_created")]
    public int SpecsCreated { get; set; }

    // Count of IntegrationAction rows discovered from the uploaded OpenAPI
    // specs. Each path+method pair in the YAML becomes one action, which
    // is what the palette + integration_action workflow nodes pick from.
    // Zero here typically means no specs were uploaded (or they parsed to
    // no operations).
    [JsonPropertyName("actions_created")]
    public int ActionsCreated { get; set; }
}
