using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Specs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// YamlSpecIndex is the singleton index the agent searches to find API
// operations. The property that matters and is pinned here: the YAML walk is
// lenient — one malformed spec must not blank the whole index — and a reload
// is a full replace of the previous snapshot.
public class YamlSpecIndexTests
{
    private const string NetboxYaml = """
        paths:
          /dcim/devices/:
            get:
              operationId: netbox_list_devices
              summary: List all devices
              description: Returns a paginated device list
              tags: [dcim, devices]
            post:
              operationId: netbox_create_device
              summary: Create a device
              tags: [dcim]
          /ipam/prefixes/:
            get:
              operationId: netbox_list_prefixes
              summary: List prefixes
              tags: [ipam]
        """;

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IAiApiSpecRepository, AiApiSpecRepository>();
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static void SeedSpec(ServiceProvider sp, string api, string yaml, bool active = true)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = api,
            Content = yaml,
            IsActive = active,
        });
        db.SaveChanges();
    }

    private static YamlSpecIndex NewIndex(ServiceProvider sp)
        => new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<YamlSpecIndex>.Instance);

    // ─── ParseOperations (the static parser the bundle endpoint reuses) ──

    [Fact]
    public void Parse_ExtractsEveryPathMethodPair()
    {
        var ops = YamlSpecIndex.ParseOperations("netbox", NetboxYaml).ToList();

        Assert.Equal(3, ops.Count);
        Assert.Equal(
            new[] { "netbox_create_device", "netbox_list_devices", "netbox_list_prefixes" },
            ops.Select(o => o.OperationId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Parse_NormalisesMethodToUppercaseAndCarriesFields()
    {
        var op = YamlSpecIndex.ParseOperations("netbox", NetboxYaml)
            .Single(o => o.OperationId == "netbox_list_devices");

        Assert.Equal("GET", op.Method);
        Assert.Equal("netbox", op.Api);
        Assert.Equal("/dcim/devices/", op.Path);
        Assert.Equal("List all devices", op.Summary);
        Assert.Equal("Returns a paginated device list", op.Description);
        Assert.Equal(new[] { "dcim", "devices" }, op.Tags);
    }

    // Without an explicit operationId the parser synthesizes "<api>:<method>_<path>"
    // so the operation is still addressable.
    [Fact]
    public void Parse_MissingOperationId_IsSynthesized()
    {
        const string yaml = """
            paths:
              /status:
                get:
                  summary: Health
            """;

        var op = Assert.Single(YamlSpecIndex.ParseOperations("acme", yaml));

        Assert.Equal("acme:get_/status", op.OperationId);
    }

    // Missing optional fields become empty strings / empty lists rather than
    // nulls, so downstream Contains() searches never NRE.
    [Fact]
    public void Parse_MissingOptionalFields_BecomeEmptyNotNull()
    {
        const string yaml = """
            paths:
              /status:
                get:
                  operationId: acme_status
            """;

        var op = Assert.Single(YamlSpecIndex.ParseOperations("acme", yaml));

        Assert.Equal(string.Empty, op.Summary);
        Assert.Equal(string.Empty, op.Description);
        Assert.Empty(op.Tags);
    }

    // Keys under a path that are not HTTP verbs (`parameters`, `servers`, vendor
    // extensions) must not become phantom operations.
    [Fact]
    public void Parse_NonHttpVerbKeys_AreIgnored()
    {
        const string yaml = """
            paths:
              /devices:
                parameters:
                  name: limit
                x-internal:
                  note: ignore me
                get:
                  operationId: acme_list
            """;

        var op = Assert.Single(YamlSpecIndex.ParseOperations("acme", yaml));

        Assert.Equal("acme_list", op.OperationId);
    }

    [Fact]
    public void Parse_AllEightHttpVerbsAreRecognised()
    {
        const string yaml = """
            paths:
              /thing:
                get: {operationId: a}
                post: {operationId: b}
                put: {operationId: c}
                patch: {operationId: d}
                delete: {operationId: e}
                head: {operationId: f}
                options: {operationId: g}
                trace: {operationId: h}
            """;

        var ops = YamlSpecIndex.ParseOperations("acme", yaml).ToList();

        Assert.Equal(8, ops.Count);
        Assert.All(ops, o => Assert.Equal(o.Method, o.Method.ToUpperInvariant()));
    }

    // A tags value that is a scalar (not a sequence) is skipped rather than
    // throwing — leniency is the whole point of this parser.
    [Fact]
    public void Parse_NonSequenceTags_YieldEmptyList()
    {
        const string yaml = """
            paths:
              /thing:
                get:
                  operationId: acme_thing
                  tags: not-a-list
            """;

        Assert.Empty(Assert.Single(YamlSpecIndex.ParseOperations("acme", yaml)).Tags);
    }

    [Theory]
    [InlineData("")]                       // empty document
    [InlineData("just-a-scalar")]          // root is not a mapping
    [InlineData("info:\n  title: x")]      // no `paths` key
    [InlineData("paths: not-a-mapping")]   // `paths` is not a mapping
    public void Parse_ShapesWithoutOperations_YieldNothing(string yaml)
    {
        Assert.Empty(YamlSpecIndex.ParseOperations("acme", yaml));
    }

    // ─── ReloadAsync + lookups ──────────────────────────────────────────

    // Before any reload the index serves empty results instead of throwing.
    [Fact]
    public void Lookups_BeforeReload_ServeEmptyIndex()
    {
        using var sp = BuildProvider(nameof(Lookups_BeforeReload_ServeEmptyIndex));
        var index = NewIndex(sp);

        Assert.Empty(index.All());
        Assert.Empty(index.Search("device"));
        Assert.Null(index.GetByOperationId("netbox_list_devices"));
    }

    [Fact]
    public async Task Reload_IndexesActiveSpecs()
    {
        using var sp = BuildProvider(nameof(Reload_IndexesActiveSpecs));
        SeedSpec(sp, "netbox", NetboxYaml);
        var index = NewIndex(sp);

        await index.ReloadAsync();

        Assert.Equal(3, index.All().Count);
        Assert.NotNull(index.GetByOperationId("netbox_list_devices"));
    }

    [Fact]
    public async Task Reload_SkipsInactiveSpecs()
    {
        using var sp = BuildProvider(nameof(Reload_SkipsInactiveSpecs));
        SeedSpec(sp, "netbox", NetboxYaml, active: false);
        var index = NewIndex(sp);

        await index.ReloadAsync();

        Assert.Empty(index.All());
    }

    // One unparseable spec must be logged and skipped, leaving its healthy
    // siblings indexed — otherwise a single bad upload blinds the whole index.
    [Fact]
    public async Task Reload_MalformedSpec_DoesNotPoisonTheRest()
    {
        using var sp = BuildProvider(nameof(Reload_MalformedSpec_DoesNotPoisonTheRest));
        SeedSpec(sp, "broken", "paths:\n  - [unclosed\n   bad: : :");
        SeedSpec(sp, "netbox", NetboxYaml);
        var index = NewIndex(sp);

        await index.ReloadAsync();

        Assert.Equal(3, index.All().Count);
        Assert.All(index.All(), o => Assert.Equal("netbox", o.Api));
    }

    // A reload is a full replace, not a merge: specs deleted since the last
    // reload must disappear from the index.
    [Fact]
    public async Task Reload_ReplacesPreviousSnapshot()
    {
        using var sp = BuildProvider(nameof(Reload_ReplacesPreviousSnapshot));
        SeedSpec(sp, "netbox", NetboxYaml);
        var index = NewIndex(sp);
        await index.ReloadAsync();
        Assert.Equal(3, index.All().Count);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var row in db.AiApiSpecs.ToList()) row.IsActive = false;
            db.SaveChanges();
        }
        await index.ReloadAsync();

        Assert.Empty(index.All());
    }

    // Two specs colliding on operationId must not throw in the ById dictionary
    // build; first one wins.
    [Fact]
    public async Task Reload_DuplicateOperationIds_KeepFirstWithoutThrowing()
    {
        const string dup = """
            paths:
              /a:
                get: {operationId: shared_op, summary: from-a}
            """;
        const string dup2 = """
            paths:
              /b:
                get: {operationId: shared_op, summary: from-b}
            """;
        using var sp = BuildProvider(nameof(Reload_DuplicateOperationIds_KeepFirstWithoutThrowing));
        SeedSpec(sp, "alpha", dup);
        SeedSpec(sp, "beta", dup2);
        var index = NewIndex(sp);

        await index.ReloadAsync();

        Assert.Equal(2, index.All().Count);
        Assert.NotNull(index.GetByOperationId("shared_op"));
    }

    // ─── Search ─────────────────────────────────────────────────────────

    private static async Task<YamlSpecIndex> LoadedIndex(ServiceProvider sp)
    {
        SeedSpec(sp, "netbox", NetboxYaml);
        var index = NewIndex(sp);
        await index.ReloadAsync();
        return index;
    }

    [Fact]
    public async Task Search_NoFilters_ReturnsEverything()
    {
        using var sp = BuildProvider(nameof(Search_NoFilters_ReturnsEverything));
        var index = await LoadedIndex(sp);

        Assert.Equal(3, index.Search("").Count);
    }

    [Theory]
    [InlineData("prefixes", 1)]   // matches operationId + path + summary
    [InlineData("PREFIXES", 1)]   // case-insensitive
    [InlineData("dcim", 2)]       // matches path and tags
    [InlineData("nonexistent", 0)]
    public async Task Search_KeywordMatchesIdPathSummaryOrTags(string keyword, int expected)
    {
        using var sp = BuildProvider($"{nameof(Search_KeywordMatchesIdPathSummaryOrTags)}-{keyword}");
        var index = await LoadedIndex(sp);

        Assert.Equal(expected, index.Search(keyword).Count);
    }

    [Fact]
    public async Task Search_KeywordMatchesTagOnly()
    {
        using var sp = BuildProvider(nameof(Search_KeywordMatchesTagOnly));
        var index = await LoadedIndex(sp);

        var hit = Assert.Single(index.Search("ipam"));
        Assert.Equal("netbox_list_prefixes", hit.OperationId);
    }

    [Fact]
    public async Task Search_FiltersByMethodCaseInsensitively()
    {
        using var sp = BuildProvider(nameof(Search_FiltersByMethodCaseInsensitively));
        var index = await LoadedIndex(sp);

        Assert.Equal(2, index.Search("", method: "get").Count);
        Assert.Equal(2, index.Search("", method: "GET").Count);
        var post = Assert.Single(index.Search("", method: "post"));
        Assert.Equal("netbox_create_device", post.OperationId);
    }

    [Fact]
    public async Task Search_FiltersByApi()
    {
        using var sp = BuildProvider(nameof(Search_FiltersByApi));
        var index = await LoadedIndex(sp);

        Assert.Equal(3, index.Search("", api: "NETBOX").Count);
        Assert.Empty(index.Search("", api: "other"));
    }

    [Fact]
    public async Task Search_CombinesFilters()
    {
        using var sp = BuildProvider(nameof(Search_CombinesFilters));
        var index = await LoadedIndex(sp);

        var hit = Assert.Single(index.Search("device", api: "netbox", method: "POST"));
        Assert.Equal("netbox_create_device", hit.OperationId);
    }

    [Fact]
    public async Task GetByOperationId_IsExactMatchNotSubstring()
    {
        using var sp = BuildProvider(nameof(GetByOperationId_IsExactMatchNotSubstring));
        var index = await LoadedIndex(sp);

        Assert.NotNull(index.GetByOperationId("netbox_list_devices"));
        Assert.Null(index.GetByOperationId("list_devices"));
        Assert.Null(index.GetByOperationId("NETBOX_LIST_DEVICES"));
    }
}
