using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using McpServerModel = flow_weaver_backend.Models.McpServer;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The intent heuristic on top of reference validation: a workflow whose stated
// purpose mentions email/Slack/notify but that ships no integration_action is
// almost certainly a python_snippet pretending to send it.
//
// These are WARNINGS, never errors — the heuristic is deliberately fuzzy, and
// blocking a save on a keyword match would be worse than the bug it catches.
public class WorkflowIntentWarningTests
{

    // The validator only consults permissions for capability-gated node types
    // (mcp_call); everything under test here is allowed either way.
    private sealed class AllowAllEffective : IEffectivePermissions
    {
        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<bool> HasAsync(string capability, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public WorkflowReferenceValidator Build() => new(
            new SnippetRepository(Db),
            new IntegrationRepository(Db),
            new RepositoryBase<IntegrationActionModel>(Db),
            new RepositoryBase<McpServerModel>(Db),
            new AllowAllEffective(),
            new FakeAppSettings(),
            NullLogger<WorkflowReferenceValidator>.Instance);

        public Guid SeedSnippet(string type)
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id,
                Name = "s-" + type,
                Type = type,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    // Ok() leaves both collections null; only OkWithWarnings/Invalid fill
    // them, so every assertion reads through these.
    private static IReadOnlyList<string> Warnings(WorkflowValidationResult r)
        => r.Warnings ?? Array.Empty<string>();

    private static IReadOnlyList<string> Errors(WorkflowValidationResult r)
        => r.Errors ?? Array.Empty<string>();

    private static string Node(string id, string snippetId)
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":" + JsonSerializer.Serialize(snippetId) + ",\"x\":0,\"y\":0}";

    private static Task<WorkflowValidationResult> Validate(
        Fixture f, string nodes, string? name = null, string? description = null)
        => f.Build().ValidateWithContextAsync(TestJson.Element(nodes), name, description, default);

    [Fact]
    public async Task AGraphOfSentinelsIsValidAndSilent()
    {
        using var f = new Fixture();

        var result = await Validate(f, "[" + Node("s", "__start__") + "]");

        Assert.True(result.IsValid);
        Assert.Empty(Warnings(result));
    }

    // The heuristic fires on the concatenated name + description, so a
    // purpose stated in either place is caught.
    [Theory]
    [InlineData("email")]
    [InlineData("e-mail")]
    [InlineData("correo")]
    [InlineData("notify")]
    [InlineData("slack")]
    [InlineData("webhook")]
    public async Task AStatedIntentWithoutAnIntegrationActionWarns(string keyword)
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]",
            description: $"this workflow will {keyword} the on-call engineer");

        Assert.True(result.IsValid);
        Assert.NotEmpty(Warnings(result));
    }

    [Fact]
    public async Task TheIntentIsAlsoReadFromTheWorkflowName()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]", name: "notify on-call");

        Assert.NotEmpty(Warnings(result));
    }

    // Matching is case-insensitive — the intent doesn't change with casing.
    [Fact]
    public async Task IntentMatchingIsCaseInsensitive()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]", description: "send an EMAIL");

        Assert.NotEmpty(Warnings(result));
    }

    // The email warning names the right way to do it, because the wrong way
    // (a python_snippet that sends mail) is exactly what SnippetService
    // rejects later — the user should not discover that at save time.
    [Fact]
    public async Task TheEmailWarningPointsAtTheIntegrationActionPath()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]", description: "send an email report");

        Assert.Contains(Warnings(result), w => w.Contains("integration_action"));
    }

    // With the right node type present there is nothing to warn about.
    [Fact]
    public async Task AnIntegrationActionNodeSilencesTheWarning()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("integration_action");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]", description: "send an email report");

        Assert.Empty(Warnings(result));
    }

    // No stated intent, no heuristic.
    [Fact]
    public async Task AWorkflowWithNoStatedIntentIsSilent()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(f, "[" + Node("a", snippetId.ToString()) + "]");

        Assert.Empty(Warnings(result));
    }

    [Fact]
    public async Task AnUnrelatedDescriptionIsSilent()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]",
            description: "collects the LLDP neighbour table from every core router");

        Assert.Empty(Warnings(result));
    }

    // A broken reference is an ERROR, and the heuristics are skipped — the
    // user has to fix the DAG before nuance is useful.
    [Fact]
    public async Task ABrokenReferenceErrorsAndSuppressesTheHeuristics()
    {
        using var f = new Fixture();

        var result = await Validate(
            f, "[" + Node("a", Guid.NewGuid().ToString()) + "]",
            description: "send an email report");

        Assert.False(result.IsValid);
        Assert.NotEmpty(Errors(result));
        Assert.Empty(Warnings(result));
    }

    // Warnings never block: the result stays valid so the save proceeds.
    [Fact]
    public async Task WarningsDoNotBlockTheSave()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet("python_snippet");

        var result = await Validate(
            f, "[" + Node("a", snippetId.ToString()) + "]", description: "notify the team");

        Assert.True(result.IsValid);
        Assert.Empty(Errors(result));
    }
}

