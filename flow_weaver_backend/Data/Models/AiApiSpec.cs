namespace flow_weaver_backend.Models;

// One row = one OpenAPI 3.x YAML file that used to live under /Specs/*.yaml.
// The API prefix used by the agent's discover/detail/execute tools equals
// the `Api` column (filename without the .yaml extension).
//
// Seed: /Specs/*.yaml is imported on boot when the table is still
// empty. Admins upload/edit via /api/admin/api-specs.
public class AiApiSpec : BaseModel
{
    public Guid AiApiSpecId { get; set; }

    // Filename without extension (e.g. "netbox"). The agent passes this
    // to discover(api="netbox", ...), so the column is unique and kept
    // lowercase-friendly.
    public string Api { get; set; } = string.Empty;

    // Raw YAML body. Parsed by YamlSpecIndex into ApiOperation[].
    public string Content { get; set; } = string.Empty;

    // Cached count of HTTP operations parsed out of Content. Recomputed
    // on every upsert so the list endpoint can display it without
    // re-parsing every row.
    public int OperationCount { get; set; }

    public Guid? CreatedBy { get; set; }

    // Optional link to an Integration. When set, the agent knows the
    // operations defined in this spec target the integration's base URL
    // and should resolve credentials from its AuthConfig. Null = global
    // spec (reusable across integrations, no automatic credential pairing).
    public Guid? IntegrationId { get; set; }
}
