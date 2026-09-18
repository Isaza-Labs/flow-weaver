using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// `${secret:...}` references are decrypted by the worker and handed to the
// step. Two rules keep that from being a way to read secrets:
//   * only someone holding secret.read may write a reference into a workflow;
//   * data that arrives while a run executes (run input, webhook bodies, step
//     outputs, device / run context) can never carry a live reference.
public class SecretMarkerTrustTests
{
    // ─── SecretMarkers ─────────────────────────────────────────────────

    [Fact]
    public void Neutralize_BreaksEveryMarkerInANestedTree()
    {
        var input = TestJson.Element("""
            { "a": "x ${secret:secret:api:value} y",
              "b": [ "${SECRET:credential:c1:password}", 3, true ],
              "c": { "d": "plain" } }
            """);

        var output = SecretMarkers.Neutralize(input);

        Assert.False(SecretMarkers.ContainsAny(output));
        Assert.Equal("plain", output.GetProperty("c").GetProperty("d").GetString());
        Assert.Equal(3, output.GetProperty("b")[1].GetInt32());
        // Still readable: only an invisible joiner was inserted.
        Assert.Equal("x ${secret:secret:api:value} y",
            output.GetProperty("a").GetString()!.Replace("⁠", ""));
    }

    [Fact]
    public void Neutralize_LeavesMarkerFreeDataUntouched()
    {
        var input = TestJson.Element("""{ "a": "no marker", "b": "$ {secret: spaced}" }""");

        var output = SecretMarkers.Neutralize(input);

        Assert.Equal(input.GetRawText(), output.GetRawText());
    }

    [Fact]
    public async Task ANeutralizedMarkerIsNotResolved()
    {
        using var db = TestDb.NewContext();
        db.Secrets.Add(new Secret
        {
            SecretId = Guid.NewGuid(), Name = "api-token", IsActive = true,
            EncryptedValue = Encoding.UTF8.GetBytes("s3cr3t"),
        });
        await db.SaveChangesAsync();
        var resolver = new SecretResolver(
            new SecretRepository(db), new CredentialRepository(db),
            new AiProviderRepository(db), new IntegrationRepository(db),
            new FakeUser(), new FakeCrypto(), new HttpContextAccessor(), new NoJwt(),
            new FakeTrace(), NullLogger<SecretResolver>.Instance);

        var neutralized = SecretMarkers.Neutralize("Bearer ${secret:secret:api-token:value}");
        var result = await resolver.SubstituteAsync(neutralized, default);

        Assert.DoesNotContain("s3cr3t", result);
        // Control: the live marker does resolve.
        Assert.Equal("Bearer s3cr3t",
            await resolver.SubstituteAsync("Bearer ${secret:secret:api-token:value}", default));
    }

    // ─── Run input as steps see it ──────────────────────────────────────

    [Theory]
    [InlineData("manual")]
    [InlineData("schedule")]
    [InlineData("webhook")]
    public void RunInputFromOutsideIsNeutralized(string trigger)
    {
        var run = new WorkflowRunModel
        {
            Trigger = trigger,
            InputPayload = TestJson.Element("""{ "password": "${secret:credential:c1:password}", "webhook": { "x": "${secret:secret:s:value}" } }"""),
        };

        Assert.False(SecretMarkers.ContainsAny(WorkflowExecutor.RunInputForSteps(run)));
    }

    // A subflow child's input is the parent step's payload: its markers were
    // written into the parent's config by an authorised author.
    [Fact]
    public void ASubflowChildKeepsItsParentsAuthoredMarkers()
    {
        var run = new WorkflowRunModel
        {
            Trigger = "subflow",
            InputPayload = TestJson.Element("""{ "password": "${secret:credential:c1:password}" }"""),
        };

        Assert.True(SecretMarkers.ContainsAny(WorkflowExecutor.RunInputForSteps(run)));
    }

    // ─── Authoring: secret.read ─────────────────────────────────────────

    private static WorkflowReferenceValidator Validator(
        AppDbContext db, string[] roles, bool holdsSecretRead, string mode = "legacy")
        => new(
            new SnippetRepository(db), new IntegrationRepository(db),
            new RepositoryBase<IntegrationAction>(db), new RepositoryBase<McpServer>(db),
            new ScriptedEffective(holdsSecretRead), new ModeSettings(mode),
            new FakeUser { Roles = roles }, NullLogger<WorkflowReferenceValidator>.Instance);

    private static (AppDbContext db, Guid snippetId) DbWithSnippet(string type = "python_snippet")
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var id = Guid.NewGuid();
        db.Snippets.Add(new Snippet { SnippetId = id, Name = type, Type = type, IsActive = true });
        db.SaveChanges();
        return (db, id);
    }

    private static JsonElement NodeWith(string snippetId, string overrides)
        => TestJson.Element("[{\"id\":\"n1\",\"snippet_id\":\"" + snippetId + "\",\"config_overrides\":" + overrides + "}]");

    [Fact]
    public async Task AnOperatorCannotSaveASecretReference()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: true)
            .ValidateAsync(NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}" }"""), default);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("n1") && e.Contains("secret.read"));
    }

    // A subflow node's config becomes the child's input, where markers are live.
    [Fact]
    public async Task AnOperatorCannotHideAReferenceInASubflowNode()
    {
        var (db, _) = DbWithSnippet();
        using var __ = db;

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(NodeWith("subflow", """{ "subflow_name": "child", "input": { "p": "${secret:credential:c1:password}" } }"""), default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task AnAdminCanSaveASecretReference()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;

        var result = await Validator(db, new[] { "admin" }, holdsSecretRead: true)
            .ValidateAsync(NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}" }"""), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task GranularModeHonoursASecretReadGrant()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: true, mode: "granular")
            .ValidateAsync(NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}" }"""), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ConfigWithoutReferencesIsUnaffected()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(NodeWith(sid.ToString(), """{ "token": "{{ input.token }}" }"""), default);

        Assert.True(result.IsValid);
    }

    // ─── only what the edit ADDS is gated ───────────────────────────────

    [Fact]
    public async Task AnOperatorCanKeepASecretReferenceThatWasAlreadySaved()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;
        var nodes = NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}" }""");

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(nodes, default, previousNodes: nodes);

        Assert.True(result.IsValid);
    }

    // Editing another field of the same node keeps the saved reference valid.
    [Fact]
    public async Task AnOperatorCanEditAroundASavedReference()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;
        var before = NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}", "retries": 1 }""");
        var after = NodeWith(sid.ToString(), """{ "retries": 3, "token": "${secret:secret:api:value}" }""");

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(after, default, previousNodes: before);

        Assert.True(result.IsValid);
    }

    // Pointing the node at a different secret is a new reference.
    [Fact]
    public async Task SwappingTheReferencedSecretIsStillGated()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;
        var before = NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}" }""");
        var after = NodeWith(sid.ToString(), """{ "token": "${secret:credential:root:password}" }""");

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(after, default, previousNodes: before);

        Assert.False(result.IsValid);
    }

    // The same reference moved onto a different node is new for that node.
    [Fact]
    public async Task MovingAReferenceToAnotherNodeIsStillGated()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;
        var before = NodeWith(sid.ToString(), """{ "token": "${secret:secret:api:value}" }""");
        var after = TestJson.Element(
            "[{\"id\":\"n2\",\"snippet_id\":\"" + sid + "\",\"config_overrides\":{\"token\":\"${secret:secret:api:value}\"}}]");

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(after, default, previousNodes: before);

        Assert.False(result.IsValid);
    }

    // Leaving the marker untouched but repointing the node at a python_snippet
    // (whose handler walks the whole payload) hands the editor the plaintext:
    // that is a new reference, not the one that was saved.
    [Fact]
    public async Task RepointingASavedReferenceAtAnotherSnippetIsGated()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var _ = db;
        var ssh = Guid.NewGuid();
        var python = Guid.NewGuid();
        db.Snippets.Add(new Snippet { SnippetId = ssh, Name = "ssh", Type = "ssh", IsActive = true });
        db.Snippets.Add(new Snippet { SnippetId = python, Name = "py", Type = "python_snippet", IsActive = true });
        db.SaveChanges();
        const string cfg = """{ "password": "${secret:credential:c1:password}" }""";

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(NodeWith(python.ToString(), cfg), default,
                previousNodes: NodeWith(ssh.ToString(), cfg));

        Assert.False(result.IsValid);
    }

    // Moving a saved reference to another field of the same node is new too.
    [Fact]
    public async Task MovingAReferenceToAnotherFieldIsGated()
    {
        var (db, sid) = DbWithSnippet();
        using var _ = db;
        var before = NodeWith(sid.ToString(), """{ "password": "${secret:credential:c1:password}" }""");
        var after = NodeWith(sid.ToString(), """{ "token": "${secret:credential:c1:password}" }""");

        var result = await Validator(db, new[] { "operator" }, holdsSecretRead: false)
            .ValidateAsync(after, default, previousNodes: before);

        Assert.False(result.IsValid);
    }

    private sealed class ScriptedEffective(bool holdsSecretRead) : IEffectivePermissions
    {
        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
            => HasAsync(capability, ct);
        public Task<bool> HasAsync(string capability, CancellationToken ct = default)
            => Task.FromResult(capability == "secret.read" ? holdsSecretRead : true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class ModeSettings(string mode) : IAppSettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = mode });
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    private sealed class NoJwt : IJwtTokenService
    {
        public string CreateAccessToken(User user, out DateTime expiresAt)
            => throw new NotSupportedException();
        public string CreateAccessToken(Guid userId, string username, IEnumerable<string> roles, out DateTime expiresAt)
            => throw new NotSupportedException();
        public string CreateAccessToken(Guid userId, string username, IEnumerable<string> roles,
            IEnumerable<string>? capabilityCeiling, out DateTime expiresAt)
            => throw new NotSupportedException();
    }
}
