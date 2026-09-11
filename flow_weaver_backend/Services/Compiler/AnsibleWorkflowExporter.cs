using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Compiler;

// Generates an Ansible playbook from a workflow. One play per workflow,
// one task per node (skipping sentinels). Snippet types map to ansible
// modules where the mapping is obvious (ping → ansible.builtin.ping,
// rest_call → ansible.builtin.uri, ssh → ansible.builtin.shell,
// ansible_playbook → embedded tasks). Other types collapse to a debug
// task with a TODO so the user sees the gap.
public sealed class AnsibleWorkflowExporter : IWorkflowExporter
{
    public string Format => "ansible";
    public string ContentType => "text/yaml";
    public string FileExtension => "yml";

    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private readonly ILogger<AnsibleWorkflowExporter> _logger;

    public AnsibleWorkflowExporter(ILogger<AnsibleWorkflowExporter> logger)
    {
        _logger = logger;
    }

    public Task<string> ExportAsync(
        WorkflowModel wf,
        IReadOnlyDictionary<Guid, SnippetModel> snippets,
        CancellationToken ct)
    {
        var nodes = ParseNodes(wf.Nodes);
        var ordered = TopoSort(nodes, ParseEdges(wf.Edges));

        var tasks = new List<object>();
        foreach (var n in ordered)
        {
            SnippetModel? snip = null;
            if (Guid.TryParse(n.SnippetId, out var sid)) snippets.TryGetValue(sid, out snip);
            var type = snip?.Type ?? n.SnippetId;
            if (type == "__start__" || type == "__end__") continue;

            tasks.Add(BuildTask(n, snip, type));
        }

        var play = new Dictionary<string, object?>
        {
            ["name"] = $"FlowWeaver workflow: {wf.Name}",
            ["hosts"] = "{{ target_hosts | default('localhost') }}",
            ["gather_facts"] = false,
            ["vars"] = new Dictionary<string, object?>
            {
                ["workflow_id"] = wf.WorkflowId.ToString(),
                ["workflow_environment"] = wf.Environment,
            },
            ["tasks"] = tasks,
        };

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"# Auto-generated from FlowWeaver workflow: {wf.Name}");
        if (!string.IsNullOrWhiteSpace(wf.Description))
            sb.AppendLine($"# Description: {wf.Description}");
        sb.AppendLine($"# Workflow id: {wf.WorkflowId}");
        sb.AppendLine($"# Generated:   {DateTime.UtcNow:O}");
        sb.AppendLine("# Override target_hosts on the command line: ansible-playbook -e 'target_hosts=routers' ...");
        sb.AppendLine();
        sb.Append(Yaml.Serialize(new[] { play }));

        _logger.LogInformation(
            "compiler.ansible.export.ok workflow_id={WorkflowId} task_count={TaskCount} output_bytes={Bytes}",
            wf.WorkflowId, tasks.Count, sb.Length);

        return Task.FromResult(sb.ToString());
    }

    private static Dictionary<string, object?> BuildTask(
        WorkflowNode node, SnippetModel? snip, string type)
    {
        var name = $"[{node.Id}] {snip?.Name ?? type}";
        switch (type)
        {
            case "ping":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.ping"] = new Dictionary<string, object?>(),
                };
            case "rest_call":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.uri"] = new Dictionary<string, object?>
                    {
                        ["url"] = "{{ rest_url | default('https://example.com') }}",
                        ["method"] = "{{ rest_method | default('GET') }}",
                        ["headers"] = "{{ rest_headers | default({}) }}",
                        ["body_format"] = "json",
                        ["return_content"] = true,
                        ["status_code"] = new[] { 200, 201, 202, 204 },
                    },
                    ["register"] = $"node_{Sanitize(node.Id)}",
                };
            case "ssh":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.shell"] = "{{ ssh_command | default('uptime') }}",
                    ["delegate_to"] = "{{ ssh_target | default(inventory_hostname) }}",
                    ["register"] = $"node_{Sanitize(node.Id)}",
                };
            case "python_snippet":
            case "transform":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.script"] = $"snippets/{Sanitize(node.Id)}.py",
                    ["register"] = $"node_{Sanitize(node.Id)}",
                    ["__comment__"] = "Drop the snippet body into snippets/" + Sanitize(node.Id) + ".py",
                };
            case "ansible_playbook":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.shell"] =
                        !string.IsNullOrWhiteSpace(snip?.Code)
                            ? "set -e\n" + snip!.Code!.Trim()
                            : "echo 'no playbook body'",
                };
            case "jmespath":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.set_fact"] = new Dictionary<string, object?>
                    {
                        [$"node_{Sanitize(node.Id)}"] =
                            "{{ (input | default({})) | community.general.json_query(query) }}",
                    },
                    ["vars"] = new Dictionary<string, object?>
                    {
                        ["query"] = !string.IsNullOrWhiteSpace(snip?.Code) ? snip!.Code!.Trim() : "@",
                    },
                };
            case "integration_action":
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.debug"] = new Dictionary<string, object?>
                    {
                        ["msg"] = "TODO: integration_action — supply the equivalent module call",
                    },
                };
            default:
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["ansible.builtin.debug"] = new Dictionary<string, object?>
                    {
                        ["msg"] = $"node {node.Id} has unsupported type: {type}",
                    },
                };
        }
    }

    private static string Sanitize(string id)
    {
        var sb = new StringBuilder();
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "node" : sb.ToString();
    }

    private static List<WorkflowNode> ParseNodes(JsonElement el)
    {
        var list = new List<WorkflowNode>();
        if (el.ValueKind != JsonValueKind.Array) return list;
        foreach (var n in el.EnumerateArray())
        {
            var id = n.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var snippetId = n.TryGetProperty("snippet_id", out var sidEl) ? sidEl.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(id))
                list.Add(new WorkflowNode(id, snippetId));
        }
        return list;
    }

    private static List<WorkflowEdge> ParseEdges(JsonElement el)
    {
        var list = new List<WorkflowEdge>();
        if (el.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in el.EnumerateArray())
        {
            var src = e.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            var tgt = e.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                list.Add(new WorkflowEdge(src, tgt));
        }
        return list;
    }

    private static List<WorkflowNode> TopoSort(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var indeg = nodes.ToDictionary(n => n.Id, _ => 0);
        var adj = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var e in edges)
        {
            if (!indeg.ContainsKey(e.Target) || !adj.ContainsKey(e.Source)) continue;
            indeg[e.Target]++;
            adj[e.Source].Add(e.Target);
        }
        var queue = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var result = new List<WorkflowNode>();
        var seen = new HashSet<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            result.Add(byId[id]);
            foreach (var next in adj[id])
            {
                indeg[next]--;
                if (indeg[next] == 0) queue.Enqueue(next);
            }
        }
        foreach (var n in nodes)
            if (seen.Add(n.Id)) result.Add(n);
        return result;
    }

    private sealed record WorkflowNode(string Id, string SnippetId);
    private sealed record WorkflowEdge(string Source, string Target);
}
