using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// S15: dependency resolver. Uses EF InMemory so we can stage real
// Snippet rows for the candidates-for-mapping path.
public class DependencyResolverTests
{
    private static AppDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Sentinel_and_existing_snippets_are_not_reported_missing()
    {
        var db = NewContext(nameof(Sentinel_and_existing_snippets_are_not_reported_missing));
        var sshId = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = sshId,
            Name = "show-version",
            Type = "ssh",
            IsActive = true,
            InputSchema = JsonDocument.Parse("{}").RootElement,
            OutputSchema = JsonDocument.Parse("{}").RootElement,
            RetryPolicy = JsonDocument.Parse("{}").RootElement,
        });
        await db.SaveChangesAsync();

        var resolver = new DependencyResolver(
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new VendorCommandRepository(db),
            new FakeUser ());
        var wf = JsonDocument.Parse($$"""
            {
              "nodes": [
                { "id": "s", "snippet_id": "__start__", "config_overrides": {} },
                { "id": "n", "snippet_id": "{{sshId}}", "config_overrides": {} },
                { "id": "e", "snippet_id": "__end__", "config_overrides": {} }
              ]
            }
            """).RootElement;

        var report = await resolver.ResolveAsync(wf, default);
        Assert.Empty(report.Snippets);
        Assert.Empty(report.Integrations);
    }

    [Fact]
    public async Task Unknown_snippet_reference_is_reported_with_inferred_type()
    {
        var db = NewContext(nameof(Unknown_snippet_reference_is_reported_with_inferred_type));
        var resolver = new DependencyResolver(
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new VendorCommandRepository(db),
            new FakeUser ());

        var wf = JsonDocument.Parse("""
            {
              "nodes": [
                { "id": "h", "snippet_id": "http_call_external", "config_overrides": {} }
              ]
            }
            """).RootElement;

        var report = await resolver.ResolveAsync(wf, default);
        Assert.Single(report.Snippets);
        Assert.Equal("rest_call", report.Snippets[0].InferredType);
        Assert.Contains("stub", report.Snippets[0].ActionsAvailable);
        Assert.Contains("generate_with_ai", report.Snippets[0].ActionsAvailable);
    }

    [Fact]
    public async Task Integration_id_collected_from_config_overrides()
    {
        var db = NewContext(nameof(Integration_id_collected_from_config_overrides));
        var resolver = new DependencyResolver(
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new VendorCommandRepository(db),
            new FakeUser ());

        var wf = JsonDocument.Parse("""
            {
              "nodes": [
                {
                  "id": "n",
                  "snippet_id": "integration_action",
                  "config_overrides": { "integration_id": "netbox_api", "base_url": "https://nb.example.com" }
                }
              ]
            }
            """).RootElement;

        var report = await resolver.ResolveAsync(wf, default);
        Assert.Single(report.Integrations);
        Assert.Equal("netbox_api", report.Integrations[0].IdInImport);
        Assert.Equal("https://nb.example.com", report.Integrations[0].InferredBaseUrl);
    }

    [Fact]
    public void InferSnippetType_returns_sensible_defaults()
    {
        Assert.Equal("rest_call", DependencyResolver.InferSnippetType("http_request"));
        Assert.Equal("ssh", DependencyResolver.InferSnippetType("ssh-cmd"));
        Assert.Equal("ansible_playbook", DependencyResolver.InferSnippetType("run-ansible"));
        Assert.Equal("python_snippet", DependencyResolver.InferSnippetType("totally_unknown"));
    }
}
