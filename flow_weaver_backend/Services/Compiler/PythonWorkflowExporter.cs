using System.Globalization;
using System.Text;
using System.Text.Json;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Compiler;

// Generates a self-contained Python script that mirrors the workflow DAG.
// Each node becomes a function; the orchestrator at the bottom invokes
// them in topological order following success edges. The output is meant
// as a starting point for users who want to run the same logic outside
// FlowWeaver — review and adapt before running.
public sealed class PythonWorkflowExporter : IWorkflowExporter
{
    public string Format => "python";
    public string ContentType => "text/x-python";
    public string FileExtension => "py";

    private readonly ILogger<PythonWorkflowExporter> _logger;

    public PythonWorkflowExporter(ILogger<PythonWorkflowExporter> logger)
    {
        _logger = logger;
    }

    public Task<string> ExportAsync(
        WorkflowModel wf,
        IReadOnlyDictionary<Guid, SnippetModel> snippets,
        CancellationToken ct)
    {
        var nodes = ParseNodes(wf.Nodes);
        var edges = ParseEdges(wf.Edges);
        var order = TopoSort(nodes, edges);

        var sb = new StringBuilder();
        WriteHeader(sb, wf);
        WriteImports(sb);
        WriteHelpers(sb);

        foreach (var node in order)
        {
            WriteNodeFunction(sb, node, snippets);
        }

        WriteOrchestrator(sb, order, edges);

        _logger.LogInformation(
            "compiler.python.export.ok workflow_id={WorkflowId} node_count={NodeCount} output_bytes={Bytes}",
            wf.WorkflowId, order.Count, sb.Length);

        return Task.FromResult(sb.ToString());
    }

    private static void WriteHeader(StringBuilder sb, WorkflowModel wf)
    {
        sb.AppendLine("\"\"\"");
        sb.AppendLine($"Auto-generated from FlowWeaver workflow: {wf.Name}");
        if (!string.IsNullOrWhiteSpace(wf.Description))
            sb.AppendLine($"Description: {wf.Description}");
        sb.AppendLine($"Workflow id: {wf.WorkflowId}");
        sb.AppendLine($"Environment: {wf.Environment}");
        sb.AppendLine($"Schema:      {wf.SchemaVersion}");
        sb.AppendLine($"Generated:   {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("This script is a starting point. Snippet bodies are embedded verbatim where");
        sb.AppendLine("available; HTTP calls and SSH steps use placeholder credentials. Review,");
        sb.AppendLine("supply secrets, and adjust target hosts before running.");
        sb.AppendLine("\"\"\"");
        sb.AppendLine();
    }

    private static void WriteImports(StringBuilder sb)
    {
        sb.AppendLine("from __future__ import annotations");
        sb.AppendLine();
        sb.AppendLine("import json");
        sb.AppendLine("import logging");
        sb.AppendLine("import subprocess");
        sb.AppendLine("import sys");
        sb.AppendLine("from typing import Any, Dict");
        sb.AppendLine();
        sb.AppendLine("# Optional dependencies — install only what your workflow uses:");
        sb.AppendLine("#   pip install requests paramiko");
        sb.AppendLine("try:");
        sb.AppendLine("    import requests  # for rest_call nodes");
        sb.AppendLine("except ImportError:");
        sb.AppendLine("    requests = None  # type: ignore");
        sb.AppendLine();
        sb.AppendLine("try:");
        sb.AppendLine("    import paramiko  # for ssh nodes");
        sb.AppendLine("except ImportError:");
        sb.AppendLine("    paramiko = None  # type: ignore");
        sb.AppendLine();
    }

    private static void WriteHelpers(StringBuilder sb)
    {
        sb.AppendLine("logging.basicConfig(level=logging.INFO, format=\"%(asctime)s %(levelname)s %(message)s\")");
        sb.AppendLine("log = logging.getLogger(\"workflow\")");
        sb.AppendLine();
        sb.AppendLine("Context = Dict[str, Any]");
        sb.AppendLine();
    }

    private static void WriteNodeFunction(
        StringBuilder sb,
        WorkflowNode node,
        IReadOnlyDictionary<Guid, SnippetModel> snippets)
    {
        var fn = SafeIdentifier(node.Id);
        SnippetModel? snip = null;
        if (Guid.TryParse(node.SnippetId, out var sid)) snippets.TryGetValue(sid, out snip);

        var label = snip?.Name ?? node.SnippetId;
        var type = snip?.Type ?? node.SnippetId;

        sb.AppendLine($"# Node `{node.Id}` — type: {type} — snippet: {label}");
        sb.AppendLine($"def step_{fn}(ctx: Context) -> Any:");

        switch (type)
        {
            case "__start__":
                sb.AppendLine("    log.info(\"workflow.start\")");
                sb.AppendLine("    return None");
                break;
            case "__end__":
                sb.AppendLine("    log.info(\"workflow.end\")");
                sb.AppendLine("    return None");
                break;
            case "ping":
                sb.AppendLine("    target = ctx.get(\"target\", \"127.0.0.1\")");
                sb.AppendLine("    log.info(\"ping %s\", target)");
                sb.AppendLine("    rc = subprocess.call([\"ping\", \"-c\", \"1\", target])");
                sb.AppendLine("    return {\"reachable\": rc == 0}");
                break;
            case "rest_call":
                sb.AppendLine("    if requests is None:");
                sb.AppendLine("        raise RuntimeError(\"pip install requests\")");
                sb.AppendLine("    method = ctx.get(\"method\", \"GET\")");
                sb.AppendLine("    url = ctx.get(\"url\", \"https://example.com\")");
                sb.AppendLine("    headers = ctx.get(\"headers\", {})");
                sb.AppendLine("    body = ctx.get(\"body\")");
                sb.AppendLine("    resp = requests.request(method, url, headers=headers, json=body, timeout=30)");
                sb.AppendLine("    resp.raise_for_status()");
                sb.AppendLine("    try:");
                sb.AppendLine("        return resp.json()");
                sb.AppendLine("    except ValueError:");
                sb.AppendLine("        return resp.text");
                break;
            case "ssh":
                sb.AppendLine("    if paramiko is None:");
                sb.AppendLine("        raise RuntimeError(\"pip install paramiko\")");
                sb.AppendLine("    host = ctx[\"host\"]");
                sb.AppendLine("    user = ctx.get(\"user\", \"admin\")");
                sb.AppendLine("    password = ctx.get(\"password\")");
                sb.AppendLine("    commands = ctx.get(\"commands\", [])");
                sb.AppendLine("    client = paramiko.SSHClient()");
                sb.AppendLine("    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())");
                sb.AppendLine("    client.connect(host, username=user, password=password, timeout=30)");
                sb.AppendLine("    out = []");
                sb.AppendLine("    for cmd in commands:");
                sb.AppendLine("        _, stdout, _ = client.exec_command(cmd)");
                sb.AppendLine("        out.append(stdout.read().decode())");
                sb.AppendLine("    client.close()");
                sb.AppendLine("    return out");
                break;
            case "python_snippet":
            case "transform":
                if (!string.IsNullOrWhiteSpace(snip?.Code))
                {
                    sb.AppendLine("    # Snippet body (python_snippet/transform). `ctx` holds previous outputs.");
                    foreach (var line in snip!.Code!.Split('\n'))
                        sb.AppendLine("    " + line.TrimEnd('\r'));
                    sb.AppendLine("    return locals().get(\"result\")");
                }
                else
                {
                    sb.AppendLine("    log.warning(\"snippet body unavailable for node %s\", \"" + Escape(node.Id) + "\")");
                    sb.AppendLine("    return None");
                }
                break;
            case "ansible_playbook":
                sb.AppendLine("    log.warning(\"ansible_playbook nodes are exported as a no-op in Python — re-export as Ansible.\")");
                sb.AppendLine("    return None");
                break;
            case "jmespath":
                sb.AppendLine("    # jmespath query — install with: pip install jmespath");
                sb.AppendLine("    import jmespath  # type: ignore");
                if (!string.IsNullOrWhiteSpace(snip?.Code))
                {
                    var expr = snip!.Code!.Trim();
                    sb.AppendLine($"    expr = {PyString(expr)}");
                }
                else
                {
                    sb.AppendLine("    expr = ctx.get(\"expression\", \"@\")");
                }
                sb.AppendLine("    return jmespath.search(expr, ctx.get(\"input\", ctx))");
                break;
            case "integration_action":
                sb.AppendLine("    # integration_action invokes a registered FlowWeaver integration. The");
                sb.AppendLine("    # integration's resolved HTTP request lives server-side; supply your own");
                sb.AppendLine("    # transport here if you want this script to run standalone.");
                sb.AppendLine("    log.info(\"integration_action node %s — supply implementation\", \"" + Escape(node.Id) + "\")");
                sb.AppendLine("    return None");
                break;
            default:
                sb.AppendLine($"    # Unhandled snippet type: {type}");
                sb.AppendLine("    log.info(\"node %s (type=%s) — no generator available\", \"" + Escape(node.Id) + "\", \"" + Escape(type) + "\")");
                sb.AppendLine("    return None");
                break;
        }
        sb.AppendLine();
    }

    private static void WriteOrchestrator(
        StringBuilder sb,
        IReadOnlyList<WorkflowNode> order,
        IReadOnlyList<WorkflowEdge> edges)
    {
        sb.AppendLine("def main(initial_ctx: Context | None = None) -> Context:");
        sb.AppendLine("    ctx: Context = dict(initial_ctx or {})");
        if (order.Count == 0)
        {
            sb.AppendLine("    log.warning(\"workflow has no nodes\")");
            sb.AppendLine("    return ctx");
        }
        else
        {
            // Linear walk in topological order, branching on success/failure
            // edges. Conditional edges are exported as TODO comments — the
            // user must wire the predicate in by hand.
            foreach (var n in order)
            {
                var fn = SafeIdentifier(n.Id);
                sb.AppendLine($"    try:");
                sb.AppendLine($"        ctx[{PyString(n.Id)}] = step_{fn}(ctx)");
                sb.AppendLine($"    except Exception as exc:");
                sb.AppendLine($"        log.error(\"node {Escape(n.Id)} failed: %s\", exc)");
                var failureEdges = edges.Where(e => e.Source == n.Id && e.Type == "failure").ToList();
                if (failureEdges.Count == 0)
                    sb.AppendLine("        raise");
                else
                {
                    sb.AppendLine("        # failure branch — original node propagates to:");
                    foreach (var fe in failureEdges)
                        sb.AppendLine($"        #   - {fe.Target}");
                    sb.AppendLine("        ctx.setdefault(\"_errors\", []).append(str(exc))");
                }
            }
            sb.AppendLine("    return ctx");
        }
        sb.AppendLine();
        sb.AppendLine("if __name__ == \"__main__\":");
        sb.AppendLine("    seed = json.loads(sys.argv[1]) if len(sys.argv) > 1 else {}");
        sb.AppendLine("    final = main(seed)");
        sb.AppendLine("    print(json.dumps(final, default=str, indent=2))");
        sb.AppendLine();
    }

    private static List<WorkflowNode> ParseNodes(JsonElement el)
    {
        var list = new List<WorkflowNode>();
        if (el.ValueKind != JsonValueKind.Array) return list;
        foreach (var n in el.EnumerateArray())
        {
            var id = n.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var snippetId = n.TryGetProperty("snippet_id", out var sidEl) ? sidEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
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
            var ty = e.TryGetProperty("type", out var ty1) ? ty1.GetString() ?? "success" : "success";
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) continue;
            list.Add(new WorkflowEdge(src, tgt, ty));
        }
        return list;
    }

    // Kahn's algorithm. Nodes not part of any edge appear in input order
    // at the end so the script lists every step even if the DAG is broken.
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
        // Append any cycles/leftovers so user still sees them.
        foreach (var n in nodes)
            if (seen.Add(n.Id)) result.Add(n);
        return result;
    }

    private static string SafeIdentifier(string id)
    {
        var sb = new StringBuilder();
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString();
        if (s.Length == 0 || char.IsDigit(s[0])) s = "n_" + s;
        return s;
    }

    private static string PyString(string s)
    {
        return "\"" + Escape(s) + "\"";
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    private sealed record WorkflowNode(string Id, string SnippetId);
    private sealed record WorkflowEdge(string Source, string Target, string Type);
}
