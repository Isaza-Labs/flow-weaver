using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Compiler;

// Converts a Workflow model into a human-readable YAML document. The
// output is a self-contained artifact that can be version-controlled,
// reviewed in a PR, or fed back into a future import endpoint.
//
// Format v1:
//   version: 1
//   workflow:
//     id: <guid>
//     name: <name>
//     environment: <env>
//     schema_version: <sv>
//   nodes: [{ id, snippet_id, type, config_overrides, x, y }]
//   edges: [{ source, target, type, condition? }]
//   input_schema: { ... }
//   metadata: { ... }
public class WorkflowYamlCompiler : IWorkflowYamlCompiler
{
    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private readonly ILogger<WorkflowYamlCompiler> _logger;

    public WorkflowYamlCompiler(ILogger<WorkflowYamlCompiler> logger)
    {
        _logger = logger;
    }

    public string Compile(WorkflowModel wf)
    {
        var nodeCount = CountArrayItems(wf.Nodes);
        var edgeCount = CountArrayItems(wf.Edges);

        _logger.LogDebug(
            "compiler.yaml.compile.start workflow_id={WorkflowId} node_count={NodeCount} edge_count={EdgeCount}",
            wf.WorkflowId, nodeCount, edgeCount);

        string yaml;
        try
        {
            var doc = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["workflow"] = new Dictionary<string, object?>
                {
                    ["id"] = wf.WorkflowId.ToString(),
                    ["name"] = wf.Name,
                    ["description"] = wf.Description,
                    ["environment"] = wf.Environment,
                    ["schema_version"] = wf.SchemaVersion,
                },
                ["nodes"] = JsonToObject(wf.Nodes),
                ["edges"] = JsonToObject(wf.Edges),
                ["input_schema"] = JsonToObject(wf.InputSchema),
                ["metadata"] = JsonToObject(wf.Metadata),
            };

            yaml = Yaml.Serialize(doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "compiler.yaml.compile.failed workflow_id={WorkflowId}",
                wf.WorkflowId);
            throw;
        }

        _logger.LogInformation(
            "compiler.yaml.compile.ok workflow_id={WorkflowId} node_count={NodeCount} output_bytes={OutputBytes}",
            wf.WorkflowId, nodeCount, yaml.Length);

        return yaml;
    }

    private static int CountArrayItems(JsonElement el) =>
        el.ValueKind == JsonValueKind.Array ? el.GetArrayLength() : 0;

    private static object? JsonToObject(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
            JsonValueKind.Array => el.EnumerateArray()
                .Select(JsonToObject).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null,
        };
    }
}
