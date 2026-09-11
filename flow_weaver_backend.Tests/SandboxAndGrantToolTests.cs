using System.Security.Claims;
using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Three small pieces with outsized consequences: the python sandbox's
// resource limits, the report table's lenient JSON reader, and the agent's
// grant tool.
public class SandboxAndGrantToolTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // ─── SandboxConfig ──────────────────────────────────────────────────

    [Fact]
    public void Sandbox_TheDefaultsAreConservative()
    {
        var config = SandboxConfig.Default;

        Assert.Equal(512, config.MaxMemoryMB);
        Assert.Equal(300, config.MaxCPUSeconds);
        Assert.Equal(10, config.MaxFileSizeMB);
        Assert.Equal(10, config.MaxProcesses);
    }

    // The preamble is injected above every user script, so it has to be
    // valid Python that only limits — never anything that could fail the run
    // on a host without the limit in question.
    [Fact]
    public void Sandbox_ThePreambleImportsResourceAndSetsEveryLimit()
    {
        var preamble = SandboxConfig.Default.ResourceLimitsPreamble();

        Assert.Contains("import resource as _fw_resource", preamble);
        Assert.Contains("RLIMIT_AS", preamble);
        Assert.Contains("RLIMIT_CPU", preamble);
        Assert.Contains("RLIMIT_FSIZE", preamble);
        Assert.Contains("RLIMIT_NPROC", preamble);
    }

    // Resource limits are POSIX-only; on a host that lacks one the preamble
    // must swallow the error rather than aborting the user's script.
    [Fact]
    public void Sandbox_EverySetrlimitIsGuarded()
    {
        var preamble = SandboxConfig.Default.ResourceLimitsPreamble();

        var attempts = preamble.Split("setrlimit").Length - 1;
        var guards = preamble.Split("except (ValueError, OSError):").Length - 1;
        Assert.Equal(attempts, guards);
    }

    [Fact]
    public void Sandbox_TheConfiguredValuesReachThePreamble()
    {
        var preamble = new SandboxConfig
        {
            MaxMemoryMB = 64, MaxCPUSeconds = 7, MaxFileSizeMB = 2, MaxProcesses = 3,
        }.ResourceLimitsPreamble();

        Assert.Contains("64 * 1024 * 1024", preamble);
        Assert.Contains("_fw_cpu_limit = 7", preamble);
        Assert.Contains("2 * 1024 * 1024", preamble);
        Assert.Contains("_fw_nproc_limit = 3", preamble);
    }

    // The helper names are cleaned up so they can't collide with anything the
    // user's script defines afterwards.
    [Fact]
    public void Sandbox_ThePreambleCleansUpItsOwnNames()
    {
        Assert.Contains(
            "del _fw_resource, _fw_mem_limit, _fw_cpu_limit, _fw_file_limit, _fw_nproc_limit",
            SandboxConfig.Default.ResourceLimitsPreamble());
    }

    // ─── LenientStringRowsConverter ─────────────────────────────────────
    //
    // The agent frequently emits report table cells as numbers or booleans
    // rather than strings. Being strict would fail the whole report over a
    // formatting detail, so the converter coerces scalars instead.

    private sealed class RowsHolder
    {
        [System.Text.Json.Serialization.JsonPropertyName("rows")]
        [System.Text.Json.Serialization.JsonConverter(typeof(LenientStringRowsConverter))]
        public List<List<string>> Rows { get; set; } = new();
    }

    private static List<List<string>> ReadRows(string json)
        => JsonSerializer.Deserialize<RowsHolder>("{\"rows\":" + json + "}")!.Rows;

    [Fact]
    public void Rows_StringCellsPassThrough()
    {
        var rows = ReadRows("""[["r1","up"],["r2","down"]]""");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "r1", "up" }, rows[0]);
    }

    // The whole point: an agent-emitted number must not fail the report.
    [Fact]
    public void Rows_NonStringScalarsAreCoerced()
    {
        var rows = ReadRows("""[["r1",42,true,null,3.5]]""");

        var row = Assert.Single(rows);
        Assert.Equal("r1", row[0]);
        Assert.Equal("42", row[1]);
        Assert.Equal(5, row.Count);
    }

    [Fact]
    public void Rows_AnEmptyTableIsAccepted()
    {
        Assert.Empty(ReadRows("[]"));
    }

    // KNOWN LIMITATION: the converter has a `TokenType == Null → new()` branch,
    // but System.Text.Json never reaches it for a reference type — it assigns
    // null directly without consulting the converter. A `"rows": null` payload
    // therefore leaves the property null rather than an empty table, so callers
    // must not assume non-null just because the converter looks like it
    // guarantees it.
    [Fact]
    public void Rows_ANullTableIsAssignedDirectlyBypassingTheConverter()
    {
        var holder = JsonSerializer.Deserialize<RowsHolder>("""{"rows":null}""")!;

        Assert.Null(holder.Rows);
    }

    [Fact]
    public void Rows_RaggedRowsAreAccepted()
    {
        var rows = ReadRows("""[["a"],["b","c","d"]]""");

        Assert.Equal(1, rows[0].Count);
        Assert.Equal(3, rows[1].Count);
    }

    // A shape that isn't a table at all is a real error — coercing it would
    // produce a nonsense report.
    [Theory]
    [InlineData("\"not a table\"")]
    [InlineData("{}")]
    [InlineData("""["not","a","row"]""")]
    public void Rows_ANonTableShapeIsRejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ReadRows(json));
    }

    [Fact]
    public void Rows_RoundTripThroughTheWriterKeepsTheShape()
    {
        var holder = new RowsHolder { Rows = new() { new() { "r1", "up" } } };

        var json = JsonSerializer.Serialize(holder);

        Assert.Equal(new[] { "r1", "up" },
            JsonSerializer.Deserialize<RowsHolder>(json)!.Rows.Single());
    }

    // ─── grant_resource_permission ──────────────────────────────────────

    private sealed class ScriptedPermissions : IResourcePermissionService
    {
        public Exception? Throw { get; set; }
        public List<(string ResourceType, Guid ResourceId, Guid SubjectId, string Role)> Grants { get; } = new();

        public Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(
            string t, Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<ResourcePermissionResponse> GrantAsync(
            string resourceType, Guid resourceId, GrantResourcePermissionRequest dto, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            Grants.Add((resourceType, resourceId, dto.SubjectId, dto.Role));
            return Task.FromResult(new ResourcePermissionResponse
            {
                ResourcePermissionId = Guid.NewGuid(),
                ResourceType = resourceType,
                ResourceId = resourceId,
                SubjectType = dto.SubjectType,
                SubjectId = dto.SubjectId,
                Role = dto.Role,
            });
        }

        public Task RevokeAsync(Guid permissionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasAtLeastAsync(string t, Guid id, string requiredRole, CancellationToken ct)
            => Task.FromResult(true);
    }

    private static GrantResourcePermissionHandler Grant(ScriptedPermissions perms)
        => new(perms, NullLogger<GrantResourcePermissionHandler>.Instance);

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    [Theory]
    [InlineData("""{"resource_id":"11111111-1111-1111-1111-111111111111","user_id":"22222222-2222-2222-2222-222222222222","role":"editor"}""", "resource_type")]
    [InlineData("""{"resource_type":"workflow","user_id":"22222222-2222-2222-2222-222222222222","role":"editor"}""", "resource_id")]
    [InlineData("""{"resource_type":"workflow","resource_id":"11111111-1111-1111-1111-111111111111","role":"editor"}""", "user_id")]
    [InlineData("""{"resource_type":"workflow","resource_id":"11111111-1111-1111-1111-111111111111","user_id":"22222222-2222-2222-2222-222222222222"}""", "role")]
    public async Task GrantTool_EveryRequiredArgumentIsChecked(string args, string expectedMention)
    {
        var perms = new ScriptedPermissions();

        var result = await Grant(perms).ExecuteAsync(TestJson.Element(args), default);

        Assert.Contains(expectedMention, Error(result));
        Assert.Empty(perms.Grants);
    }

    [Fact]
    public async Task GrantTool_ForwardsTheGrantAsAUserSubject()
    {
        var perms = new ScriptedPermissions();
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var result = await Grant(perms).ExecuteAsync(TestJson.Element(
            "{\"resource_type\":\"workflow\",\"resource_id\":\"" + resourceId
            + "\",\"user_id\":\"" + userId + "\",\"role\":\"editor\"}"), default);

        Assert.True(result.GetProperty("granted").GetBoolean());
        var call = Assert.Single(perms.Grants);
        Assert.Equal("workflow", call.ResourceType);
        Assert.Equal(resourceId, call.ResourceId);
        Assert.Equal(userId, call.SubjectId);
        Assert.Equal("editor", call.Role);
    }

    // The service's own validation (unknown resource type, unknown role)
    // must reach the model as a readable error rather than an exception.
    [Fact]
    public async Task GrantTool_AServiceValidationErrorBecomesAPayload()
    {
        var perms = new ScriptedPermissions
        {
            Throw = new ArgumentException("unknown role 'superuser'"),
        };

        var result = await Grant(perms).ExecuteAsync(TestJson.Element(
            "{\"resource_type\":\"workflow\",\"resource_id\":\"" + Guid.NewGuid()
            + "\",\"user_id\":\"" + Guid.NewGuid() + "\",\"role\":\"superuser\"}"), default);

        Assert.Contains("unknown role", Error(result));
    }

    [Fact]
    public async Task GrantTool_AnUnexpectedFailureAlsoBecomesAPayload()
    {
        var perms = new ScriptedPermissions { Throw = new InvalidOperationException("db down") };

        var result = await Grant(perms).ExecuteAsync(TestJson.Element(
            "{\"resource_type\":\"workflow\",\"resource_id\":\"" + Guid.NewGuid()
            + "\",\"user_id\":\"" + Guid.NewGuid() + "\",\"role\":\"editor\"}"), default);

        Assert.Contains("grant failed", Error(result));
    }
}
