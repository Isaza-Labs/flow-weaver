using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Policy;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// AI tool handlers implement IToolHandler: Name + ParametersSchema + ExecuteAsync(args)->JsonElement.
// These drive each read-oriented handler's ExecuteAsync over an InMemory DB and assert it returns a
// JSON payload (object/array) without throwing, plus the tool's Name/schema.
public class AiListHandlerTests
{
    private readonly FakeUser _caller = new();
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    private static void AssertJson(JsonElement e) =>
        Assert.True(e.ValueKind is JsonValueKind.Object or JsonValueKind.Array, $"unexpected {e.ValueKind}");

    [Fact]
    public async Task list_workflows()
    {
        using var db = TestDb.NewContext();
        var h = new ListWorkflowsHandler(new WorkflowRepository(db), _caller, TestAppLinks.Relative(), NullLogger<ListWorkflowsHandler>.Instance);
        Assert.Equal("list_workflows", h.Name);
        Assert.Equal(JsonValueKind.Object, h.ParametersSchema.ValueKind);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    // Every row carries the page link, and the summary fields keep the exact
    // names the model already sees — the projection that adds `Url` must not
    // quietly re-case the rest.
    [Fact]
    public async Task list_workflows_rows_carry_the_page_link()
    {
        using var db = TestDb.NewContext();
        var id = Guid.NewGuid();
        db.Workflows.Add(new flow_weaver_backend.Models.Workflow
        {
            WorkflowId = id,
            Name = "daily-backup",
            Environment = "production",
            Version = 4,
            SchemaVersion = "v1",
            Nodes = TestJson.Element("[]"),
            Edges = TestJson.Element("[]"),
            Metadata = TestJson.Element("{}"),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var h = new ListWorkflowsHandler(new WorkflowRepository(db), _caller, TestAppLinks.Relative(), NullLogger<ListWorkflowsHandler>.Instance);
        var row = (await h.ExecuteAsync(Empty, CancellationToken.None)).EnumerateArray().Single();

        Assert.Equal($"/workflows/{id}", row.GetProperty("Url").GetString());
        Assert.Equal("daily-backup", row.GetProperty("Name").GetString());
        Assert.Equal("production", row.GetProperty("Environment").GetString());
        Assert.Equal(4, row.GetProperty("Version").GetInt32());
        Assert.Equal(id, row.GetProperty("WorkflowId").GetGuid());
    }

    [Fact]
    public async Task list_snippets()
    {
        using var db = TestDb.NewContext();
        var h = new ListSnippetsHandler(new SnippetRepository(db), _caller, NullLogger<ListSnippetsHandler>.Instance);
        Assert.Equal("list_snippets", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task list_credentials()
    {
        using var db = TestDb.NewContext();
        var h = new ListCredentialsHandler(new CredentialRepository(db), _caller, NullLogger<ListCredentialsHandler>.Instance);
        Assert.Equal("list_credentials", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task list_vendor_commands()
    {
        using var db = TestDb.NewContext();
        var h = new ListVendorCommandsHandler(new VendorCommandRepository(db), _caller, NullLogger<ListVendorCommandsHandler>.Instance);
        Assert.Equal("list_vendor_commands", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task list_plan_features()
    {
        using var db = TestDb.NewContext();
        var h = new ListPlanFeaturesHandler(new PlanFeatureRepository(db), _caller, NullLogger<ListPlanFeaturesHandler>.Instance);
        Assert.Equal("list_plan_features", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task list_apis()
    {
        var h = new ListApisHandler(new FakeApiSpecIndex(), _caller, NullLogger<ListApisHandler>.Instance);
        Assert.Equal("list_apis", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task list_policies()
    {
        using var db = TestDb.NewContext();
        var svc = new PolicyService(new RepositoryBase<flow_weaver_backend.Models.Policy>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance);
        var h = new ListPoliciesHandler(svc, NullLogger<ListPoliciesHandler>.Instance);
        Assert.Equal("list_policies", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task list_users()
    {
        using var db = TestDb.NewContext();
        var h = new ListUsersHandler(db, _caller, NullLogger<ListUsersHandler>.Instance);
        Assert.Equal("list_users", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }

    [Fact]
    public async Task query_devices()
    {
        using var db = TestDb.NewContext();
        var h = new QueryDevicesHandler(new DeviceRepository(db), _caller, NullLogger<QueryDevicesHandler>.Instance);
        Assert.Equal("query_devices", h.Name);
        AssertJson(await h.ExecuteAsync(Empty, CancellationToken.None));
    }
}
