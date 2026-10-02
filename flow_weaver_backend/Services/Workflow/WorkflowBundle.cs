using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Services.Workflow;

/// <summary>
/// The portable, self-contained form of a workflow — what one FlowWeaver
/// instance hands to another, and what the cross-product workflow.v1
/// interchange standard (bundle/SPEC.md) fixes as the only portable form.
/// </summary>
/// <remarks>
/// <para>
/// The v1 export serialized <c>nodes</c> verbatim, which meant it carried
/// nothing but per-instance GUIDs. On a second instance none of them resolved,
/// every dependency read as missing, and the import wizard's only remedy for a
/// missing snippet was a fabricated placeholder — which is how sharing a
/// workflow produced python stubs instead of wiring the integration that was
/// already there.
/// </para>
/// <para>
/// v2 kept the nodes verbatim (so a re-import into the SAME instance is exact)
/// and added a <c>dependencies</c> section that gives every referenced GUID a
/// portable identity. The importer resolves those identities locally and
/// rewrites the GUIDs; the node shape never changes, so there is exactly one
/// place where identity is translated.
/// </para>
/// <para>
/// v3 is v2 made complete. It declares what it needs (<c>requires</c>) so an
/// importer refuses precisely instead of failing at run time; it writes the
/// portable NAME key beside every local id (<c>integration</c> beside
/// <c>integration_id</c>, <c>server</c> beside <c>mcp_server_id</c>, …) so a
/// node is meaningful without the <c>dependencies</c> table; it flattens every
/// sub-workflow reachable through <c>subflow</c> nodes into
/// <c>dependencies.workflows</c>; and it carries the workflow's triggers. A v2
/// bundle is still read — its <c>requires</c> is inferred from what is visible.
/// </para>
/// <para>
/// <b>Secrets never travel.</b> An integration is described by slug, name, type
/// and base URL — never its auth config, headers or credentials; an MCP server,
/// credential or git repository by identity only; a webhook trigger without its
/// signing secret. The receiving instance must already hold its own
/// credentials for each of those, which is also why a missing one is a hard
/// error rather than something the import invents.
/// </para>
/// </remarks>
public sealed class WorkflowBundle
{
    /// <summary>Format marker this build emits.</summary>
    public const string CurrentSchemaVersion = "v3";

    /// <summary>Format markers this build reads.</summary>
    public static readonly string[] AcceptedSchemaVersions = ["v2", CurrentSchemaVersion];

    /// <summary>Distinguishes a bundle from any other JSON with nodes/edges.</summary>
    public const string KindMarker = "flow_weaver.workflow_bundle";

    /// <summary>
    /// Markers this build will parse. The wire format is shared with other engines
    /// that adopted it rather than forking it; a bundle stamped with an older
    /// marker must still be readable here or half the point of the format is
    /// gone.
    /// </summary>
    public static readonly string[] AcceptedKinds =
        [KindMarker, "nashira.workflow_bundle", "netora.workflow_bundle"];

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindMarker;

    [JsonPropertyName("exported_at")]
    public DateTime ExportedAt { get; set; }

    // Informational only: which product and build wrote the file. Never used
    // to pick a code path — the schema_version and the declared `requires`
    // are what the importer dispatches on.
    [JsonPropertyName("exported_by")]
    public BundleExportedBy? ExportedBy { get; set; }

    // What the importer must support. Absent on a v2 bundle; the reader
    // infers it from the nodes and dependencies it can see.
    [JsonPropertyName("requires")]
    public BundleRequires? Requires { get; set; }

    [JsonPropertyName("workflow")]
    public BundleWorkflow Workflow { get; set; } = new();

    [JsonPropertyName("nodes")]
    public JsonElement Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement Edges { get; set; }

    [JsonPropertyName("dependencies")]
    public BundleDependencies Dependencies { get; set; } = new();

    [JsonPropertyName("triggers")]
    public List<BundleTrigger> Triggers { get; set; } = new();

    [JsonIgnore]
    public bool IsV3 => string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal);
}

public sealed class BundleExportedBy
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = "flow-weaver";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// The bundle's declaration of what it needs (bundle/SPEC.md §2). An importer
/// that lacks a listed snippet type or capability refuses up front, naming it,
/// instead of importing something that fails on its first run.
/// </summary>
public sealed class BundleRequires
{
    [JsonPropertyName("snippet_types")]
    public List<string> SnippetTypes { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("secrets")]
    public List<BundleSecretRef> Secrets { get; set; } = new();
}

/// <summary>
/// A <c>${secret:&lt;source&gt;:&lt;name&gt;:&lt;field&gt;}</c> reference found in the
/// bundle. Only the reference travels — never a value.
/// </summary>
public sealed class BundleSecretRef
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("used_by")]
    public List<string> UsedBy { get; set; } = new();
}

/// <summary>
/// The closed capability vocabulary (bundle/SPEC.md §2.2).
/// </summary>
/// <remarks>
/// Two lists, because they answer two different questions. <see cref="Known"/>
/// is the vocabulary: a name outside it is refused because this build cannot
/// know what it means. <see cref="Implemented"/> is what this instance actually
/// does: a name inside the vocabulary but outside this list is refused too —
/// same code, honest reason. Collapsing the two is what let
/// <c>rest_catalog</c> be "noted" as a degradation this product cannot in fact
/// degrade, which is the outcome the capability block exists to prevent.
/// </remarks>
public static class BundleCapabilities
{
    public const string Subflow = "subflow";
    public const string TemplateFilters = "template_filters";
    public const string RunNamespace = "run_namespace";
    public const string PerDeviceScope = "per_device_scope";
    public const string MaxParallel = "max_parallel";
    public const string PerPool = "per_pool";
    public const string ConditionalEdges = "conditional_edges";
    public const string PythonNetwork = "python_network";
    public const string Triggers = "triggers";

    /// <summary>
    /// The catalogued <c>rest_call</c> form (<c>source</c> / <c>operation_id</c>).
    /// Non-degradable, and NOT implemented here: this product's equivalent is an
    /// <c>integration_action</c> node, so a bundle declaring it is refused.
    /// </summary>
    public const string RestCatalog = "rest_catalog";

    /// <summary>Every name the vocabulary defines.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Subflow, TemplateFilters, RunNamespace, PerDeviceScope, MaxParallel,
        PerPool, ConditionalEdges, PythonNetwork, Triggers, RestCatalog,
    };

    /// <summary>The subset this instance implements. The rest are refused.</summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(StringComparer.Ordinal)
    {
        Subflow, TemplateFilters, RunNamespace, PerDeviceScope, MaxParallel,
        PerPool, ConditionalEdges, PythonNetwork, Triggers,
    };

    /// <summary>The declared capabilities this instance cannot honour.</summary>
    public static IReadOnlyList<string> Unsupported(IEnumerable<string> declared)
        => declared.Where(c => !Implemented.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
}

public sealed class BundleWorkflow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // Exported for information only. An import always lands as a draft — a
    // bundle must never be able to place a workflow straight into production
    // on someone else's instance.
    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }
}

public sealed class BundleDependencies
{
    [JsonPropertyName("snippets")]
    public List<BundleSnippet> Snippets { get; set; } = new();

    [JsonPropertyName("integrations")]
    public List<BundleIntegration> Integrations { get; set; } = new();

    [JsonPropertyName("mcp_servers")]
    public List<BundleMcpServer> McpServers { get; set; } = new();

    [JsonPropertyName("credentials")]
    public List<BundleCredential> Credentials { get; set; } = new();

    [JsonPropertyName("repositories")]
    public List<BundleRepository> Repositories { get; set; } = new();

    // Sub-workflows, flattened: every workflow reachable through `subflow`
    // nodes, transitively, once each (bundle/SPEC.md §5.4).
    [JsonPropertyName("workflows")]
    public List<BundleWorkflowDefinition> Workflows { get; set; } = new();
}

/// <summary>
/// A referenced snippet, complete. The definition travels so the receiving
/// instance can recreate it faithfully instead of stubbing it — that is the
/// difference between "the workflow you sent me works" and "here is a
/// placeholder that returns nothing".
/// </summary>
public sealed class BundleSnippet
{
    /// <summary>Source-instance GUID. Used only to remap nodes.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("script_language")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("output_schema")]
    public JsonElement? OutputSchema { get; set; }

    [JsonPropertyName("target_mode")]
    public string TargetMode { get; set; } = string.Empty;

    [JsonPropertyName("max_parallel")]
    public int MaxParallel { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("idempotency")]
    public string? Idempotency { get; set; }

    // Canonical shape (execution/SPEC.md §3): max_retries,
    // initial_delay_seconds, backoff, max_delay_seconds. The legacy
    // shape (max_attempts, delay_seconds, backoff) is accepted on read and
    // translated — see RetryPolicyTranslation.
    [JsonPropertyName("retry_policy")]
    public JsonElement? RetryPolicy { get; set; }

    [JsonPropertyName("logic_diagram_mermaid")]
    public string? LogicDiagramMermaid { get; set; }

    // Carried so the importer can REFUSE it rather than silently grant it:
    // network_enabled lifts the python sandbox's network isolation and is
    // admin-only to set locally. A bundle from elsewhere must not be able to
    // hand itself that capability.
    [JsonPropertyName("network_enabled")]
    public bool NetworkEnabled { get; set; }
}

/// <summary>
/// A referenced integration, described by identity only — never by credential.
/// </summary>
public sealed class BundleIntegration
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    // Informational: helps a human confirm they are pointing at the same
    // system. Never used to create an integration automatically.
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("actions")]
    public List<BundleAction> Actions { get; set; } = new();
}

/// <summary>
/// An action is identified portably by its owning integration plus its name.
/// </summary>
public sealed class BundleAction
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>An MCP server by identity: name and transport. No URL, no auth.</summary>
public sealed class BundleMcpServer
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }
}

/// <summary>A credential by identity. Username is a label; no secret material.</summary>
public sealed class BundleCredential
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("auth_method")]
    public string? AuthMethod { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

/// <summary>A git repository by identity: name and remote URL. Never its auth credential.</summary>
public sealed class BundleRepository
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; set; }
}

/// <summary>
/// A sub-workflow, complete: its graph travels so the receiving instance can
/// create it before the parent that calls it (bundle/SPEC.md §5.4).
/// </summary>
public sealed class BundleWorkflowDefinition
{
    /// <summary>Source-instance GUID; what `subflow_workflow_id` points at.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }

    [JsonPropertyName("nodes")]
    public JsonElement Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement Edges { get; set; }
}

/// <summary>
/// A trigger without anything local: no signing secret, no target devices,
/// no run statistics (bundle/SPEC.md §6). Created disabled on import.
/// </summary>
public sealed class BundleTrigger
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("cron_expression")]
    public string? CronExpression { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("allow_unsigned")]
    public bool AllowUnsigned { get; set; }

    [JsonPropertyName("allow_target_override")]
    public bool AllowTargetOverride { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("input_defaults")]
    public JsonElement? InputDefaults { get; set; }

    // Informational: the source's state. The importer always creates the
    // trigger disabled — enabling it is an explicit local act.
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("notify_on")]
    public List<string> NotifyOn { get; set; } = new();

    [JsonPropertyName("notification_webhook_url")]
    public string? NotificationWebhookUrl { get; set; }
}
