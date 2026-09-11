using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.AiApiSpec;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ApiSpecModel = flow_weaver_backend.Models.AiApiSpec;

namespace flow_weaver_backend.Tests;

// Uploading an OpenAPI spec. Two things must hold: the payload is validated
// BEFORE it is persisted (a spec that breaks the parser would poison the index
// on the next reload), and the in-memory operation index is reloaded after
// every mutation so the agent's discover_operations sees the change without a
// restart. The unique (api) index spans soft-deleted rows, so a
// re-upload after a delete has to reactivate in place.
public class AiApiSpecServiceWriteTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    private sealed class RecordingIndex : IApiSpecIndex
    {
        public int Reloaded { get; set; }
        public Task ReloadAsync(CancellationToken ct = default)
        {
            Reloaded++;
            return Task.CompletedTask;
        }
        public IReadOnlyList<ApiOperation> All() => Array.Empty<ApiOperation>();
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null)
            => Array.Empty<ApiOperation>();
        public ApiOperation? GetByOperationId(string operationId) => null;
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingIndex Index { get; } = new();

        public AiApiSpecService Build() => new(
            new AiApiSpecRepository(Db),
            new FakeUser(),
            Index,
            NullLogger<AiApiSpecService>.Instance);

        public Guid SeedSpec(string api = "netbox", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.AiApiSpecs.Add(new ApiSpecModel
            {
                AiApiSpecId = id,
                Api = api,
                Content = "paths: {}",
                OperationCount = 0,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private const string TwoOpSpec = """
        paths:
          /devices:
            get:
              operationId: list_devices
            post:
              operationId: create_device
        """;

    private static CreateAiApiSpec Create(
        string api = "netbox", string content = TwoOpSpec, Guid? integrationId = null)
        => new() { Api = api, Content = content, IntegrationId = integrationId };

    private static int StatusOf<T>(ActionResult<T> result)
        => Assert.IsType<ObjectResult>(result.Result) is var obj && obj.StatusCode is { } code
            ? code
            : throw new InvalidOperationException("no status code");

    private static AiApiSpecResponse Created(ActionResult<AiApiSpecResponse> result)
        => Assert.IsType<AiApiSpecResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);

    private static AiApiSpecResponse Ok(ActionResult<AiApiSpecResponse> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<AiApiSpecResponse>(result.Value);
    }

    // ─── payload validation (before any write) ──────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingApiIdentifierIsRejected(string api)
    {
        using var f = new Fixture();

        Assert.IsType<BadRequestObjectResult>((await f.Build().PostAsync(Create(api: api))).Result);
        Assert.Empty(f.Db.AiApiSpecs);
    }

    [Theory]
    [InlineData("net box")]
    [InlineData("net.box")]
    [InlineData("net/box")]
    [InlineData("net@box")]
    public async Task InvalidApiIdentifierIsRejected(string api)
    {
        using var f = new Fixture();

        Assert.IsType<BadRequestObjectResult>((await f.Build().PostAsync(Create(api: api))).Result);
    }

    [Theory]
    [InlineData("netbox")]
    [InlineData("net_box")]
    [InlineData("net-box")]
    [InlineData("NetBox2")]
    public async Task ValidApiIdentifiersAreAccepted(string api)
    {
        using var f = new Fixture();

        Assert.IsType<CreatedAtActionResult>((await f.Build().PostAsync(Create(api: api))).Result);
    }

    // A spec that breaks the YAML parser would poison the index on the next
    // reload, so it must never be persisted.
    [Fact]
    public async Task UnparseableYamlIsRejectedAndNothingIsWritten()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(Create(content: "paths:\n  - [unclosed\n bad: : :"));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("invalid YAML", bad.Value!.ToString());
        Assert.Empty(f.Db.AiApiSpecs);
        Assert.Equal(0, f.Index.Reloaded);
    }

    [Fact]
    public async Task OversizedContentIsRejected()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(Create(content: new string('x', 3 * 1024 * 1024)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── create ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePersistsWithTheOperationCount()
    {
        using var f = new Fixture();

        var body = Created(await f.Build().PostAsync(Create()));

        Assert.Equal("netbox", body.Api);
        Assert.Equal(2, body.OperationCount);
        var row = await f.Db.AiApiSpecs.SingleAsync();
        Assert.Equal(User, row.CreatedBy);
        Assert.True(row.IsActive);
    }

    // The identifier is normalised so `NetBox` and `netbox` are one spec.
    [Fact]
    public async Task ApiIdentifierIsTrimmedAndLowercased()
    {
        using var f = new Fixture();

        var body = Created(await f.Build().PostAsync(Create(api: "  NetBox  ")));

        Assert.Equal("netbox", body.Api);
    }

    // The count must match what the index will actually load, or the list UI
    // lies about how many operations the agent can call.
    [Theory]
    [InlineData("paths: {}", 0)]
    [InlineData("info:\n  title: x", 0)]
    [InlineData("", 0)]
    public async Task OperationCountForSpecsWithoutOperations(string content, int expected)
    {
        using var f = new Fixture();

        var body = Created(await f.Build().PostAsync(Create(content: content)));

        Assert.Equal(expected, body.OperationCount);
    }

    // Non-verb keys under a path (parameters, vendor extensions) are not
    // operations.
    [Fact]
    public async Task OperationCountIgnoresNonVerbKeys()
    {
        using var f = new Fixture();
        var content = """
            paths:
              /devices:
                parameters:
                  name: limit
                x-internal:
                  note: ignore
                get:
                  operationId: list
            """;

        Assert.Equal(1, Created(await f.Build().PostAsync(Create(content: content))).OperationCount);
    }

    [Fact]
    public async Task CreateReloadsTheIndex()
    {
        using var f = new Fixture();

        await f.Build().PostAsync(Create());

        Assert.Equal(1, f.Index.Reloaded);
    }

    [Fact]
    public async Task IntegrationScopeIsPersisted()
    {
        using var f = new Fixture();
        var integrationId = Guid.NewGuid();

        var body = Created(await f.Build().PostAsync(Create(integrationId: integrationId)));

        Assert.Equal(integrationId, body.IntegrationId);
    }

    // ─── collisions and re-upload ───────────────────────────────────────

    [Fact]
    public async Task ActiveDuplicateIsAConflict()
    {
        using var f = new Fixture();
        f.SeedSpec("netbox");

        var result = await f.Build().PostAsync(Create(api: "netbox"));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Single(f.Db.AiApiSpecs);
    }

    // The unique index spans soft-deleted rows, so a re-upload after a delete
    // must reactivate in place instead of colliding.
    [Fact]
    public async Task TombstonedSpecIsReactivatedInPlace()
    {
        using var f = new Fixture();
        var id = f.SeedSpec("netbox", active: false);
        var integrationId = Guid.NewGuid();

        var body = Ok(await f.Build().PostAsync(Create(api: "netbox", integrationId: integrationId)));

        Assert.Equal(id, body.AiApiSpecId);
        var row = Assert.Single(f.Db.AiApiSpecs);
        Assert.True(row.IsActive);
        Assert.Equal(2, row.OperationCount);
        Assert.Equal(integrationId, row.IntegrationId);
        Assert.Equal(User, row.CreatedBy);
    }

    [Fact]
    public async Task ReactivationAlsoReloadsTheIndex()
    {
        using var f = new Fixture();
        f.SeedSpec("netbox", active: false);

        await f.Build().PostAsync(Create(api: "netbox"));

        Assert.Equal(1, f.Index.Reloaded);
    }

    // ─── read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdReturnsTheSpecWithItsContent()
    {
        using var f = new Fixture();
        var created = Created(await f.Build().PostAsync(Create()));

        var body = Ok(await f.Build().GetByIdAsync(created.AiApiSpecId));

        Assert.Equal("netbox", body.Api);
        Assert.Contains("/devices", body.Content);
    }

    [Fact]
    public async Task GetByIdUnknownIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(Guid.NewGuid())).Result);
    }

    // The list omits the (potentially large) content blob.
    [Fact]
    public async Task ListOmitsTheContentBlob()
    {
        using var f = new Fixture();
        await f.Build().PostAsync(Create());

        var ok = Assert.IsType<OkObjectResult>((await f.Build().GetAsync()).Result);
        var body = Assert.IsType<ListResponse<AiApiSpecResponse>>(ok.Value);

        Assert.Equal(1, body.Total);
        Assert.True(string.IsNullOrEmpty(Assert.Single(body.Data).Content));
    }

    // ─── update ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUnknownIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>(
            (await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateAiApiSpec())).Result);
    }

    [Fact]
    public async Task UpdateRecountsOperationsAndReloadsTheIndex()
    {
        using var f = new Fixture();
        var created = Created(await f.Build().PostAsync(Create()));
        f.Index.Reloaded = 0;

        var body = Ok(await f.Build().UpdateAsync(created.AiApiSpecId, new UpdateAiApiSpec
        {
            Content = "paths:\n  /ping:\n    get:\n      operationId: ping",
        }));

        Assert.Equal(1, body.OperationCount);
        Assert.Equal(1, f.Index.Reloaded);
    }

    [Fact]
    public async Task UpdateWithUnparseableYamlIsRejected()
    {
        using var f = new Fixture();
        var created = Created(await f.Build().PostAsync(Create()));

        var result = await f.Build().UpdateAsync(created.AiApiSpecId, new UpdateAiApiSpec
        {
            Content = "paths:\n  - [unclosed\n bad: : :",
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(2, (await f.Db.AiApiSpecs.SingleAsync()).OperationCount);   // unchanged
    }

    // ─── delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteIsASoftDeleteThatReloadsTheIndex()
    {
        using var f = new Fixture();
        var created = Created(await f.Build().PostAsync(Create()));
        f.Index.Reloaded = 0;

        await f.Build().DeleteAsync(created.AiApiSpecId);

        Assert.False((await f.Db.AiApiSpecs.SingleAsync()).IsActive);
        Assert.Equal(1, f.Index.Reloaded);
    }

    [Fact]
    public async Task DeleteUnknownIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().DeleteAsync(Guid.NewGuid())).Result);
    }
}
