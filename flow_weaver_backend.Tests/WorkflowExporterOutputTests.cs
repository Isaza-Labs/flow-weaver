using System.Text.Json;
using flow_weaver_backend.Services.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The two "take this workflow away with you" exporters. The contract from
// IWorkflowExporter is what these pin: sentinel ids and unresolved snippet
// references must be tolerated (the export endpoint hands over whatever it
// could load), and an unknown type must degrade into a visible placeholder
// rather than silently vanishing from the artifact.
//
// The generated text is not compared verbatim — that would break on every
// wording tweak. What is asserted is the structure a user depends on: one
// function per node, topological ordering, and the per-type body actually
// referencing the right library or snippet code.
public class WorkflowExporterOutputTests
{

    private static PythonWorkflowExporter Python()
        => new(NullLogger<PythonWorkflowExporter>.Instance);

    private static AnsibleWorkflowExporter Ansible()
        => new(NullLogger<AnsibleWorkflowExporter>.Instance);

    private static WorkflowModel Workflow(string nodes, string edges = "[]", string name = "lldp-sync")
        => new()
        {
            WorkflowId = Guid.NewGuid(),
            Name = name,
            Description = "sync the LLDP table",
            Environment = "draft",
            Nodes = TestJson.Element(nodes),
            Edges = TestJson.Element(edges),
            IsActive = true,
        };

    private static string Node(string id, string snippetId)
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":" + JsonSerializer.Serialize(snippetId) + ",\"x\":0,\"y\":0}";

    private static string Edge(string source, string target, string type = "success")
        => "{\"source\":" + JsonSerializer.Serialize(source)
           + ",\"target\":" + JsonSerializer.Serialize(target)
           + ",\"type\":\"" + type + "\"}";

    private static (Guid Id, SnippetModel Snippet) Snippet(string type, string? code = null, string name = "step")
    {
        var id = Guid.NewGuid();
        return (id, new SnippetModel
        {
            SnippetId = id,
            Name = name,
            Type = type,
            Code = code,
            IsActive = true,
        });
    }

    private static Task<string> ExportPython(WorkflowModel wf, params (Guid Id, SnippetModel Snippet)[] snippets)
        => Python().ExportAsync(wf, snippets.ToDictionary(s => s.Id, s => s.Snippet), default);

    private static Task<string> ExportAnsible(WorkflowModel wf, params (Guid Id, SnippetModel Snippet)[] snippets)
        => Ansible().ExportAsync(wf, snippets.ToDictionary(s => s.Id, s => s.Snippet), default);

    // ─── Format metadata ────────────────────────────────────────────────

    // The export endpoint keys off these to pick the exporter, set
    // Content-Type and name the downloaded file.
    [Fact]
    public void TheExportersDeclareTheirFormatContentTypeAndExtension()
    {
        Assert.Equal("python", Python().Format);
        Assert.Equal("text/x-python", Python().ContentType);
        Assert.Equal("py", Python().FileExtension);

        Assert.Equal("ansible", Ansible().Format);
        Assert.Equal("text/yaml", Ansible().ContentType);
        Assert.Equal("yml", Ansible().FileExtension);
    }

    // ─── Python: shape ──────────────────────────────────────────────────

    [Fact]
    public async Task Python_EmitsAHeaderImportsAndAMainEntryPoint()
    {
        var script = await ExportPython(Workflow("[" + Node("s", "__start__") + "]"));

        Assert.Contains("lldp-sync", script);
        Assert.Contains("sync the LLDP table", script);
        Assert.Contains("import json", script);
        Assert.Contains("def main(", script);
        Assert.Contains("if __name__ == \"__main__\":", script);
    }

    [Fact]
    public async Task Python_EmitsOneFunctionPerNode()
    {
        var script = await ExportPython(Workflow(
            "[" + Node("s", "__start__") + "," + Node("fetch-data", "__end__") + "]"));

        Assert.Contains("def step_s(", script);
        // Node ids that aren't legal identifiers are sanitised.
        Assert.Contains("def step_fetch_data(", script);
    }

    // The orchestrator walks nodes in topological order — a script that
    // called a downstream step first would read its predecessor's output
    // before it existed.
    [Fact]
    public async Task Python_TheOrchestratorCallsStepsInTopologicalOrder()
    {
        var script = await ExportPython(Workflow(
            "[" + Node("c", "__end__") + "," + Node("a", "__start__") + "," + Node("b", "__end__") + "]",
            "[" + Edge("a", "b") + "," + Edge("b", "c") + "]"));

        var body = script[script.IndexOf("def main(", StringComparison.Ordinal)..];
        var a = body.IndexOf("step_a(ctx)", StringComparison.Ordinal);
        var b = body.IndexOf("step_b(ctx)", StringComparison.Ordinal);
        var c = body.IndexOf("step_c(ctx)", StringComparison.Ordinal);
        Assert.True(a >= 0 && a < b && b < c, body);
    }

    // An empty graph still produces a runnable script rather than a
    // syntactically broken `def main():` with no body.
    [Fact]
    public async Task Python_AnEmptyGraphStillProducesARunnableMain()
    {
        var script = await ExportPython(Workflow("[]"));

        Assert.Contains("workflow has no nodes", script);
        Assert.Contains("return ctx", script);
    }

    // Without a failure edge a node's exception must propagate — swallowing
    // it would let the script report success on a failed run.
    [Fact]
    public async Task Python_ANodeWithNoFailureEdgeRerraises()
    {
        var script = await ExportPython(Workflow("[" + Node("a", "__start__") + "]"));

        Assert.Contains("        raise", script);
    }

    [Fact]
    public async Task Python_AFailureEdgeCollectsTheErrorInsteadOfReraising()
    {
        var script = await ExportPython(Workflow(
            "[" + Node("a", "__start__") + "," + Node("comp", "__end__") + "]",
            "[" + Edge("a", "comp", "failure") + "]"));

        Assert.Contains("_errors", script);
        Assert.Contains("#   - comp", script);
    }

    // ─── Python: per-type bodies ────────────────────────────────────────

    [Fact]
    public async Task Python_RestCallUsesRequests()
    {
        var rest = Snippet("rest_call");
        var script = await ExportPython(Workflow("[" + Node("a", rest.Id.ToString()) + "]"), rest);

        Assert.Contains("requests.request(", script);
        Assert.Contains("raise_for_status", script);
    }

    [Fact]
    public async Task Python_SshUsesParamiko()
    {
        var ssh = Snippet("ssh");
        var script = await ExportPython(Workflow("[" + Node("a", ssh.Id.ToString()) + "]"), ssh);

        Assert.Contains("paramiko.SSHClient()", script);
        Assert.Contains("exec_command(cmd)", script);
    }

    [Fact]
    public async Task Python_PingShellsOutToPing()
    {
        var ping = Snippet("ping");
        var script = await ExportPython(Workflow("[" + Node("a", ping.Id.ToString()) + "]"), ping);

        Assert.Contains("subprocess.call([\"ping\"", script);
    }

    // The snippet body is inlined, indented into the function.
    [Fact]
    public async Task Python_ASnippetBodyIsInlinedAndIndented()
    {
        var snippet = Snippet("python_snippet", "result = {\"ok\": True}\nprint(result)");
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("    result = {\"ok\": True}", script);
        Assert.Contains("    print(result)", script);
        Assert.Contains("return locals().get(\"result\")", script);
    }

    // A snippet whose body the endpoint couldn't load must still produce a
    // valid function — with a warning the user can act on.
    [Fact]
    public async Task Python_AnEmptySnippetBodyDegradesToAWarning()
    {
        var snippet = Snippet("python_snippet", code: null);
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("snippet body unavailable", script);
        Assert.Contains("return None", script);
    }

    [Fact]
    public async Task Python_AJmespathSnippetInlinesItsExpression()
    {
        var snippet = Snippet("jmespath", "devices[*].hostname");
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("import jmespath", script);
        Assert.Contains("expr = \"devices[*].hostname\"", script);
    }

    [Fact]
    public async Task Python_AJmespathSnippetWithoutABodyReadsTheExpressionFromContext()
    {
        var snippet = Snippet("jmespath", code: null);
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("expr = ctx.get(\"expression\", \"@\")", script);
    }

    // Ansible has no Python equivalent — say so loudly instead of emitting
    // something that pretends to work.
    [Fact]
    public async Task Python_AnAnsibleNodeIsAnExplicitNoOp()
    {
        var snippet = Snippet("ansible_playbook", "- hosts: all");
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("re-export as Ansible", script);
    }

    [Fact]
    public async Task Python_AnIntegrationActionAsksTheUserToSupplyTransport()
    {
        var snippet = Snippet("integration_action");
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("supply implementation", script);
    }

    // An unknown type must show up in the artifact as a named placeholder —
    // dropping it would silently change what the script does.
    [Fact]
    public async Task Python_AnUnknownTypeBecomesAVisiblePlaceholder()
    {
        var snippet = Snippet("quantum_teleport");
        var script = await ExportPython(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("Unhandled snippet type: quantum_teleport", script);
        Assert.Contains("def step_a(", script);
    }

    // A node referencing a snippet the endpoint couldn't load: the id itself
    // becomes the type, and the export still completes.
    [Fact]
    public async Task Python_AnUnresolvedSnippetReferenceIsToleratedAsRequiredByTheContract()
    {
        var script = await ExportPython(Workflow("[" + Node("a", Guid.NewGuid().ToString()) + "]"));

        Assert.Contains("def step_a(", script);
        Assert.Contains("no generator available", script);
    }

    // A quote in the node id must not break out of the generated string
    // literal.
    [Fact]
    public async Task Python_ANodeIdWithAQuoteIsEscaped()
    {
        var script = await ExportPython(Workflow("[" + Node("a\"b", "__start__") + "]"));

        Assert.DoesNotContain("ctx[\"a\"b\"]", script);
        Assert.Contains("a\\\"b", script);
    }

    // ─── Ansible ────────────────────────────────────────────────────────

    [Fact]
    public async Task Ansible_EmitsASinglePlayWithAHeaderAndVars()
    {
        var yaml = await ExportAnsible(Workflow("[" + Node("s", "__start__") + "]"));

        Assert.StartsWith("---", yaml);
        Assert.Contains("# Auto-generated from FlowWeaver workflow: lldp-sync", yaml);
        Assert.Contains("# Description: sync the LLDP table", yaml);
        Assert.Contains("workflow_environment: draft", yaml);
        Assert.Contains("target_hosts | default('localhost')", yaml);
    }

    // Sentinels carry no work; emitting them as tasks would put two no-op
    // debug steps in every playbook.
    [Fact]
    public async Task Ansible_SentinelNodesProduceNoTasks()
    {
        var yaml = await ExportAnsible(Workflow(
            "[" + Node("s", "__start__") + "," + Node("e", "__end__") + "]",
            "[" + Edge("s", "e") + "]"));

        Assert.DoesNotContain("[s]", yaml);
        Assert.DoesNotContain("[e]", yaml);
    }

    [Fact]
    public async Task Ansible_TasksAreEmittedInTopologicalOrder()
    {
        var first = Snippet("ping", name: "reach");
        var second = Snippet("ssh", name: "collect");
        var yaml = await ExportAnsible(
            Workflow(
                "[" + Node("b", second.Id.ToString()) + "," + Node("a", first.Id.ToString()) + "]",
                "[" + Edge("a", "b") + "]"),
            first, second);

        Assert.True(
            yaml.IndexOf("[a] reach", StringComparison.Ordinal)
            < yaml.IndexOf("[b] collect", StringComparison.Ordinal), yaml);
    }

    [Theory]
    [InlineData("ping", "ansible.builtin.ping")]
    [InlineData("rest_call", "ansible.builtin.uri")]
    [InlineData("ssh", "ansible.builtin.shell")]
    [InlineData("python_snippet", "ansible.builtin.script")]
    [InlineData("transform", "ansible.builtin.script")]
    [InlineData("jmespath", "ansible.builtin.set_fact")]
    [InlineData("integration_action", "ansible.builtin.debug")]
    public async Task Ansible_EachTypeMapsToItsModule(string type, string module)
    {
        var snippet = Snippet(type);
        var yaml = await ExportAnsible(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains(module, yaml);
    }

    [Fact]
    public async Task Ansible_APlaybookSnippetInlinesItsBodyUnderSetE()
    {
        var snippet = Snippet("ansible_playbook", "  echo hello\n");
        var yaml = await ExportAnsible(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("set -e", yaml);
        Assert.Contains("echo hello", yaml);
    }

    [Fact]
    public async Task Ansible_APlaybookSnippetWithoutABodyDegradesGracefully()
    {
        var snippet = Snippet("ansible_playbook", code: null);
        var yaml = await ExportAnsible(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("no playbook body", yaml);
    }

    [Fact]
    public async Task Ansible_AJmespathSnippetBecomesTheQueryVar()
    {
        var snippet = Snippet("jmespath", "devices[*].hostname");
        var yaml = await ExportAnsible(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("json_query(query)", yaml);
        Assert.Contains("devices[*].hostname", yaml);
    }

    // The register name has to be a legal Ansible variable, so the node id
    // is sanitised the same way the Python exporter sanitises identifiers.
    [Fact]
    public async Task Ansible_RegisterNamesAreSanitisedFromTheNodeId()
    {
        var snippet = Snippet("ssh");
        var yaml = await ExportAnsible(Workflow("[" + Node("fetch-data", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("node_fetch_data", yaml);
    }

    [Fact]
    public async Task Ansible_AnUnknownTypeBecomesAVisibleDebugTask()
    {
        var snippet = Snippet("quantum_teleport");
        var yaml = await ExportAnsible(Workflow("[" + Node("a", snippet.Id.ToString()) + "]"), snippet);

        Assert.Contains("unsupported type: quantum_teleport", yaml);
    }

    [Fact]
    public async Task Ansible_AnUnresolvedSnippetReferenceIsTolerated()
    {
        var yaml = await ExportAnsible(Workflow("[" + Node("a", Guid.NewGuid().ToString()) + "]"));

        Assert.Contains("unsupported type", yaml);
    }

    [Fact]
    public async Task Ansible_AWorkflowWithoutADescriptionOmitsThatHeaderLine()
    {
        var wf = Workflow("[]");
        wf.Description = null;

        var yaml = await ExportAnsible(wf);

        Assert.DoesNotContain("# Description:", yaml);
    }

    // Both exporters have to survive a nodes/edges blob that isn't an array
    // — legacy rows and hand-edited JSON both show up in the wild.
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"nodes\"")]
    public async Task BothExportersTolerateANonArrayGraph(string nodes)
    {
        var wf = Workflow(nodes, nodes);

        Assert.Contains("workflow has no nodes", await ExportPython(wf));
        Assert.Contains("FlowWeaver workflow", await ExportAnsible(wf));
    }
}
