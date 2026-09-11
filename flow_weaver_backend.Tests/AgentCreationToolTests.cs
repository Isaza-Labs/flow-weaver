using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Snippet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PolicyModel = flow_weaver_backend.Models.Policy;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The agent's write tools plus the spec-detail lookup.
//
// These wrap the same services the REST API uses, which is the point: the
// agent must not be able to create something a human couldn't. Every rejection
// the service makes has to reach the model as a readable payload — an
// exception escaping here aborts the chat turn and the user sees nothing.
public class AgentCreationToolTests
{

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static bool Created(JsonElement result)
        => result.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.True;

    // ─── create_snippet ─────────────────────────────────────────────────

    private sealed class SnippetFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeUser Caller { get; set; }
            = new() { Roles = new[] { "operator" } };

        public CreateSnippetHandler Build() => new(
            new SnippetService(
                new SnippetRepository(Db),
                new StepRunRepository(Db),
                Caller,
                new FakeAudit(),
                new FakeTrace(),
                NullLogger<SnippetService>.Instance),
            NullLogger<CreateSnippetHandler>.Instance);

        public void Dispose() => Db.Dispose();
    }

    private const string Diagram = "graph TD\\n  A-->B";

    private static string SnippetArgs(
        string name = "collect", string type = "ssh", string targetMode = "once",
        string? extra = null)
        => "{\"name\":" + JsonSerializer.Serialize(name)
           + ",\"type\":" + JsonSerializer.Serialize(type)
           + ",\"target_mode\":" + JsonSerializer.Serialize(targetMode)
           + ",\"logic_diagram_mermaid\":\"" + Diagram + "\""
           + (extra is null ? "" : "," + extra)
           + "}";

    [Fact]
    public async Task CreateSnippet_PersistsAndReportsTheNewId()
    {
        using var f = new SnippetFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(SnippetArgs()), default);

        Assert.True(Created(result));
        var saved = Assert.Single(f.Db.Snippets);
        Assert.Equal(saved.SnippetId, result.GetProperty("snippet_id").GetGuid());
        Assert.Equal("collect", result.GetProperty("name").GetString());
        Assert.Equal("ssh", result.GetProperty("type").GetString());
    }

    // The service's validation rejections must surface as a payload, never as
    // an exception that kills the turn.
    [Theory]
    [InlineData("", "ssh", "once")]
    [InlineData("collect", "", "once")]
    [InlineData("collect", "ssh", "")]
    public async Task CreateSnippet_AServiceRejectionComesBackAsAPayload(
        string name, string type, string targetMode)
    {
        using var f = new SnippetFixture();

        var result = await f.Build().ExecuteAsync(
            TestJson.Element(SnippetArgs(name, type, targetMode)), default);

        Assert.False(Created(result));
        Assert.NotNull(Error(result));
        Assert.Empty(f.Db.Snippets);
    }

    // The agent must not be able to grant itself host networking — the same
    // admin-only guard the REST API applies.
    [Fact]
    public async Task CreateSnippet_TheAgentCannotBypassTheNetworkEnabledGuard()
    {
        using var f = new SnippetFixture { Caller = new FakeUser { Roles = new[] { "operator" } } };

        var result = await f.Build().ExecuteAsync(TestJson.Element(
            SnippetArgs(type: "python_snippet", extra: "\"network_enabled\":true")), default);

        Assert.False(Created(result));
        Assert.Empty(f.Db.Snippets);
    }

    [Fact]
    public async Task CreateSnippet_AnAdminMaySetNetworkEnabled()
    {
        using var f = new SnippetFixture { Caller = new FakeUser { Roles = new[] { "admin" } } };

        var result = await f.Build().ExecuteAsync(TestJson.Element(
            SnippetArgs(type: "python_snippet", extra: "\"network_enabled\":true,\"code\":\"import netmiko\"")),
            default);

        Assert.True(Created(result));
        Assert.True(result.GetProperty("network_enabled").GetBoolean());
    }

    [Fact]
    public async Task CreateSnippet_OptionalNumericArgumentsAreForwarded()
    {
        using var f = new SnippetFixture();

        await f.Build().ExecuteAsync(TestJson.Element(
            SnippetArgs(extra: "\"max_parallel\":7,\"timeout_seconds\":120")), default);

        var saved = Assert.Single(f.Db.Snippets);
        Assert.Equal(7, saved.MaxParallel);
        Assert.Equal(120, saved.TimeoutSeconds);
    }

    // A non-object schema would be nonsense downstream; it must not be
    // forwarded as one.
    [Fact]
    public async Task CreateSnippet_ANonObjectSchemaIsNotForwarded()
    {
        using var f = new SnippetFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(
            SnippetArgs(extra: "\"input_schema\":\"not an object\"")), default);

        Assert.True(Created(result));
        var saved = Assert.Single(f.Db.Snippets);
        Assert.NotEqual(JsonValueKind.String, saved.InputSchema.ValueKind);
    }

    // ─── create_policy ──────────────────────────────────────────────────

    private sealed class PolicyFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public CreatePolicyHandler Build() => new(
            new PolicyService(
                new RepositoryBase<PolicyModel>(Db),
                new FakeUser(),
                new FakeAudit(), NullLogger<PolicyService>.Instance),
            NullLogger<CreatePolicyHandler>.Instance);

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    public async Task CreatePolicy_ANameIsRequired(string args)
    {
        using var f = new PolicyFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("name is required", Error(result));
        Assert.Empty(f.Db.Policies);
    }

    // A policy without a rule object would gate nothing; the error says
    // exactly what shape is expected.
    [Theory]
    [InlineData("""{"name":"gate"}""")]
    [InlineData("""{"name":"gate","rule":"deny"}""")]
    [InlineData("""{"name":"gate","rule":[]}""")]
    public async Task CreatePolicy_ARuleObjectIsRequired(string args)
    {
        using var f = new PolicyFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Contains("rule is required", Error(result));
        Assert.Empty(f.Db.Policies);
    }

    [Fact]
    public async Task CreatePolicy_PersistsAndReportsTheNewId()
    {
        using var f = new PolicyFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element("""
            {"name":"no-prod-deletes","description":"d",
             "rule":{"action":"deny","when":{"env":["production"]}}}
            """), default);

        Assert.True(Created(result));
        var saved = Assert.Single(f.Db.Policies);
        Assert.Equal("no-prod-deletes", saved.Name);
        Assert.Equal("deny", saved.Rule.GetProperty("action").GetString());
        Assert.Equal(saved.PolicyId, result.GetProperty("policy_id").GetGuid());
    }

    // A policy the agent creates is live by default — that is what makes the
    // "create a gate for me" flow useful.
    [Fact]
    public async Task CreatePolicy_IsEnabledByDefault()
    {
        using var f = new PolicyFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(
            """{"name":"gate","rule":{"action":"deny"}}"""), default);

        Assert.True(result.GetProperty("enabled").GetBoolean());
        Assert.True(f.Db.Policies.Single().Enabled);
    }

    [Fact]
    public async Task CreatePolicy_CanBeCreatedDisabled()
    {
        using var f = new PolicyFixture();

        await f.Build().ExecuteAsync(TestJson.Element(
            """{"name":"gate","rule":{"action":"deny"},"enabled":false}"""), default);

        Assert.False(f.Db.Policies.Single().Enabled);
    }

    [Fact]
    public async Task CreatePolicy_TheRuleIsStoredVerbatim()
    {
        using var f = new PolicyFixture();

        await f.Build().ExecuteAsync(TestJson.Element("""
            {"name":"gate","rule":{"action":"gate","on":"promote","from":"qa","to":"production"}}
            """), default);

        var rule = f.Db.Policies.Single().Rule;
        Assert.Equal("promote", rule.GetProperty("on").GetString());
        Assert.Equal("qa", rule.GetProperty("from").GetString());
    }

    // ─── operation_detail ───────────────────────────────────────────────

    private sealed class StubIndex : IApiSpecIndex
    {
        public List<ApiOperation> Operations { get; } = new();
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => Operations;
        public IReadOnlyList<ApiOperation> Search(
            string keyword, string? api = null, string? method = null) => Operations;
        public ApiOperation? GetByOperationId(string operationId)
            => Operations.FirstOrDefault(o => o.OperationId == operationId);
    }

    private sealed class DetailFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public StubIndex Index { get; } = new();

        public OperationDetailHandler Build() => new(
            new AiApiSpecRepository(Db),
            Index,
            new FakeUser(),
            NullLogger<OperationDetailHandler>.Instance);

        public void SeedOperation(string api = "netbox", string path = "/dcim/devices/")
            => Index.Operations.Add(new ApiOperation
            {
                OperationId = $"{api}:listDevices",
                Api = api,
                Method = "GET",
                Path = path,
                Summary = "list devices",
                Description = "returns every device",
            });

        public void SeedSpec(string api, string content, bool active = true)
        {
            Db.AiApiSpecs.Add(new AiApiSpec
            {
                AiApiSpecId = Guid.NewGuid(),
                Api = api,
                Content = content,
                IsActive = active,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private const string Spec = """
        openapi: 3.0.0
        paths:
          /dcim/devices/:
            get:
              summary: list devices
              parameters:
                - name: limit
                  in: query
                  schema:
                    type: integer
        """;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"operation_id":123}""")]
    [InlineData("""{"operation_id":null}""")]
    public async Task OperationDetail_AMissingOperationIdIsAnErrorPayload(string args)
    {
        using var f = new DetailFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("operation_id is required", Error(result));
    }

    [Fact]
    public async Task OperationDetail_AnUnknownOperationIsReported()
    {
        using var f = new DetailFixture();

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"operation_id":"netbox:nope"}"""), default);

        Assert.Contains("not found", Error(result));
    }

    // The index and the stored spec are separate; an index hit with no spec
    // gets its own message so the admin knows to re-upload.
    [Fact]
    public async Task OperationDetail_AKnownOperationWithNoStoredSpecIsReported()
    {
        using var f = new DetailFixture();
        f.SeedOperation();

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"operation_id":"netbox:listDevices"}"""), default);

        Assert.Contains("not available", Error(result));
    }

    [Fact]
    public async Task OperationDetail_ASoftDeletedSpecIsNotAvailable()
    {
        using var f = new DetailFixture();
        f.SeedOperation();
        f.SeedSpec("netbox", Spec, active: false);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"operation_id":"netbox:listDevices"}"""), default);

        Assert.Contains("not available", Error(result));
    }

    [Fact]
    public async Task OperationDetail_ReturnsTheOperationMetadataAndItsSchemaSlice()
    {
        using var f = new DetailFixture();
        f.SeedOperation();
        f.SeedSpec("netbox", Spec);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"operation_id":"netbox:listDevices"}"""), default);

        Assert.Null(Error(result));
        Assert.Equal("netbox:listDevices", result.GetProperty("operation_id").GetString());
        Assert.Equal("GET", result.GetProperty("method").GetString());
        Assert.Equal("/dcim/devices/", result.GetProperty("path").GetString());
        Assert.Equal("list devices", result.GetProperty("summary").GetString());
        Assert.True(result.TryGetProperty("parameters", out _));
    }

    // Unlike discover_operations — which falls back to the compact view for
    // one bad op so the rest of the result survives — this handler has a
    // single spec to slice, so a malformed one propagates. ToolDispatcher
    // catches it and turns it into a `tool execution failed` output, which is
    // what actually reaches the model.
    [Fact]
    public async Task OperationDetail_MalformedSpecYamlPropagatesToTheDispatcher()
    {
        using var f = new DetailFixture();
        f.SeedOperation();
        f.SeedSpec("netbox", "paths:\n  - this: [is not\n   valid yaml");

        await Assert.ThrowsAnyAsync<Exception>(() => f.Build().ExecuteAsync(
            TestJson.Element("""{"operation_id":"netbox:listDevices"}"""), default));
    }
}
