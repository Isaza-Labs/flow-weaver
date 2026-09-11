using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace flow_weaver_backend.Services.Validation;

// Loads the schema once at construction (Singleton lifetime) and evaluates
// every {nodes, edges} pair against it.
//
// A failure is reported twice, from one walk of the evaluation tree: as the plain strings
// the controller has always embedded in the 400 response's `details` array, and as
// addressable violations carrying the keyword that failed and a pointer to where. The
// second is what lets the conformance gate watch this path rather than the schema file in
// isolation, and what lets an editor mark the offending node without parsing prose.
//
// Singleton is not a preference: JsonSchema.Net registers the document by its `$id` in a
// process-wide registry and refuses a second registration of the same id. Constructing a
// second instance throws.
public class WorkflowSchemaValidator : IWorkflowSchemaValidator
{
    private const string SchemaResourceSuffix = "workflow.v1.schema.json";

    private readonly JsonSchema _schema;
    private readonly string _rawJson;
    private readonly ILogger<WorkflowSchemaValidator> _logger;

    public string CurrentSchemaVersion => "v1";

    public string RawJson => _rawJson;

    public WorkflowSchemaValidator(ILogger<WorkflowSchemaValidator> logger)
    {
        _logger = logger;
        (_schema, _rawJson) = LoadSchemaWithSource();
    }

    public WorkflowValidationResult Validate(JsonElement nodes, JsonElement edges)
    {
        var nodeCount = nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;
        var edgeCount = edges.ValueKind == JsonValueKind.Array ? edges.GetArrayLength() : 0;

        try
        {
            // Defaults if the caller sent null/undefined — treat as empty arrays
            // so the schema's "required nodes + edges" stays satisfiable.
            var nodesNode = ToJsonNode(nodes) ?? new JsonArray();
            var edgesNode = ToJsonNode(edges) ?? new JsonArray();

            var root = new JsonObject
            {
                ["nodes"] = nodesNode,
                ["edges"] = edgesNode,
            };

            // JsonSchema.Net evaluates JsonElement, not JsonNode. Round-trip
            // through a JsonDocument — the payloads are tiny, cost is negligible.
            using var doc = JsonDocument.Parse(root.ToJsonString());
            var result = _schema.Evaluate(doc.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
            });

            if (result.IsValid)
            {
                _logger.LogDebug(
                    "validation.schema.ok node_count={NodeCount} edge_count={EdgeCount}",
                    nodeCount, edgeCount);
                return WorkflowValidationResult.Ok();
            }

            // One walk, two shapes. The messages are what clients already receive; the
            // violations are the same failures with the keyword and the pointer kept instead
            // of dissolved into the text. Deriving both from one pass is what stops them
            // disagreeing.
            var violations = Violations(result).ToList();
            var errors = violations.Select(Describe).ToList();
            if (errors.Count == 0)
                errors.Add("workflow shape does not match the v1 schema");

            _logger.LogWarning(
                "validation.schema.failed node_count={NodeCount} edge_count={EdgeCount} error_count={ErrorCount}",
                nodeCount, edgeCount, errors.Count);
            return WorkflowValidationResult.Invalid(errors, violations);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "validation.schema.failed node_count={NodeCount} edge_count={EdgeCount}",
                nodeCount, edgeCount);
            throw;
        }
    }

    private static JsonNode? ToJsonNode(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => null,
            _ => JsonNode.Parse(element.GetRawText()),
        };
    }

    // The message exactly as `details` has always carried it: "<pointer>: <message>", with
    // "(root)" standing in for the empty pointer. Kept as its own function so the wire format
    // has one definition rather than being an incidental side effect of a loop.
    private static string Describe(SchemaViolation v) =>
        $"{(string.IsNullOrEmpty(v.Path) ? "(root)" : v.Path)}: {v.Message}";

    private static IEnumerable<SchemaViolation> Violations(EvaluationResults root)
    {
        // JsonSchema.Net returns a tree of evaluation results. anyOf /
        // oneOf members appear as children even when the parent ends up
        // valid (because a different branch passed). Walking ALL
        // children naively produces phantom errors like
        // "/nodes/1/snippet_id: Value should match one of the values
        // specified by the enum" for every Guid string — the uuid-format
        // branch passed, but the enum branch still reports its failure.
        // The fix: only descend into invalid parents. If a node is
        // valid, its descendants are by definition unnecessary detail.
        foreach (var node in WalkInvalid(root))
        {
            if (node.Errors is null || node.Errors.Count == 0) continue;
            var pointer = node.InstanceLocation.ToString();
            // `kvp.Key` is the schema keyword that rejected the value and `pointer` is where
            // it did so. Both were already here and were being discarded into a string.
            foreach (var kvp in node.Errors)
                yield return new SchemaViolation(kvp.Key, pointer, kvp.Value);
        }
    }

    private static IEnumerable<EvaluationResults> WalkInvalid(EvaluationResults r)
    {
        if (r.IsValid) yield break;
        yield return r;
        if (r.Details is null) yield break;
        foreach (var child in r.Details)
        foreach (var descendant in WalkInvalid(child))
            yield return descendant;
    }

    private static (JsonSchema schema, string rawJson) LoadSchemaWithSource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(SchemaResourceSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded resource '{SchemaResourceSuffix}' not found. " +
                "Check csproj <EmbeddedResource> glob.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Resource stream '{resourceName}' is null.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return (JsonSchema.FromText(json), json);
    }
}
