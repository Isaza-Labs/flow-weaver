using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// A handler that exists only to be COUNTED. The bundle resolver asks the
// registry "is there a handler for this snippet type?" and refuses the import
// when there is not (bundle/SPEC.md §2.1); nothing in that path executes a
// step, so a type name is the whole of what a test needs to stand in for one.
internal sealed class RegisteredHandler(string type) : ISnippetHandler
{
    public string Type { get; } = type;

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        => throw new NotSupportedException("registry stand-in — not executable");
}

internal static class BundleTest
{
    // The types this build ships handlers for. A test that wants to pin the
    // "no handler here" refusal passes a shorter list.
    public static readonly string[] AllHandlerTypes =
    [
        "python_snippet", "ssh", "ping", "rest_call", "transform", "integration_action",
        "mcp_call", "git", "report", "email_send", "slack_message", "ansible_playbook",
    ];

    public static WorkflowBundleService Svc(AppDbContext db, params string[] handlerTypes)
        => new(new WorkflowRepository(db),
               new SnippetRepository(db),
               new IntegrationRepository(db),
               new IntegrationActionRepository(db),
               new McpServerRepository(db),
               new CredentialRepository(db),
               new GitRepositoryRepository(db),
               new WorkflowTriggerRepository(db),
               (handlerTypes.Length == 0 ? AllHandlerTypes : handlerTypes)
                   .Select(t => (ISnippetHandler)new RegisteredHandler(t))
                   .ToList(),
               new FakeUser(),
               NullLogger<WorkflowBundleService>.Instance);

    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static string Serialize(WorkflowBundle bundle)
        => JsonSerializer.Serialize(bundle, WorkflowBundleReader.SerializerOptions);

    // The canonical WIRE form: the bundle's own nodes+edges (bundle/SPEC.md
    // §8 — never the stored workflow row, which may carry whatever local
    // vocabulary this product runs on), with the only two ids a v3 bundle
    // still carries normalized.
    //
    // `snippet_id` and `subflow_workflow_id` are structural and are remapped
    // to the receiving instance's rows by design, so §8 normalizes them
    // (`normalize: ["remap:snippet_ids", "remap:workflow_ids"]`) rather than
    // expecting equality: each is replaced by its order of first appearance.
    // Every OTHER reference is a portable name and must match byte for byte —
    // which is exactly what §4 forbids local ids for.
    public static string WireForm(JsonElement nodes, JsonElement edges)
    {
        var text = CanonicalJson.Serialize(nodes) + "\n" + CanonicalJson.Serialize(edges);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            "\"(snippet_id|subflow_workflow_id)\":\"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\"",
            m =>
            {
                var id = m.Groups[2].Value;
                if (!seen.TryGetValue(id, out var alias))
                    seen[id] = alias = $"<id-{seen.Count}>";
                return $"\"{m.Groups[1].Value}\":\"{alias}\"";
            });
    }

    /// <summary>The canonical hash §8 compares, over <see cref="WireForm"/>.</summary>
    public static string WireHash(JsonElement nodes, JsonElement edges)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(WireForm(nodes, edges)))).ToLowerInvariant();
}
