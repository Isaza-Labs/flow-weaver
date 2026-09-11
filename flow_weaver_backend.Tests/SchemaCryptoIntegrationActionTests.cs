using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.IntegrationAction;
using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// WorkflowSchemaValidator registers its schema in JsonSchema.Net's PROCESS-WIDE
// registry when constructed, and a second registration throws
// ("Overwriting registered schemas is not permitted"). One instance per test
// process is therefore the only workable arrangement.
internal static class SharedSchemaValidator
{
    public static readonly WorkflowSchemaValidator Instance =
        new(NullLogger<WorkflowSchemaValidator>.Instance);
}

// Three low-level pieces every write path depends on: the v1 schema gate, the
// credential cipher, and the integration-action catalogue.
//
// The schema validator is what stands between a malformed graph and the
// executor. Anything it lets through becomes a run-time crash on a live
// device, so the cases worth pinning are the malformed ones.
public class SchemaCryptoIntegrationActionTests
{

    // ─── WorkflowSchemaValidator ────────────────────────────────────────

    private static WorkflowValidationResult Validate(string nodes, string edges = "[]")
        => SharedSchemaValidator.Instance.Validate(TestJson.Element(nodes), TestJson.Element(edges));

    private static string Node(string id = "a", string snippetId = "__start__", string type = "task")
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":" + JsonSerializer.Serialize(snippetId)
           + ",\"x\":0,\"y\":0,\"type\":\"" + type + "\"}";

    [Fact]
    public void Schema_AWellFormedGraphIsValid()
    {
        var result = Validate(
            "[" + Node("a") + "," + Node("b", "__end__") + "]",
            """[{"source":"a","target":"b","type":"success"}]""");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Schema_AnEmptyGraphIsValid()
    {
        Assert.True(Validate("[]").IsValid);
    }

    // A null/undefined blob is treated as an empty array so the schema's
    // "nodes + edges required" rule stays satisfiable for legacy rows.
    [Fact]
    public void Schema_ANullGraphDegradesToEmptyRatherThanCrashing()
    {
        Assert.True(Validate("null", "null").IsValid);
    }

    // An object where an array belongs is not "absent" — it is the wrong
    // shape, and the schema says so rather than coercing it.
    [Fact]
    public void Schema_AnObjectWhereAnArrayBelongsIsRejected()
    {
        Assert.False(Validate("{}", "{}").IsValid);
    }

    // The v1 schema uses additionalProperties:false, so a stray key from a
    // foreign exporter is a hard failure — that is exactly why the import
    // pipeline normalises before validating.
    [Fact]
    public void Schema_AStrayNodeKeyIsRejected()
    {
        var result = Validate("""[{"id":"a","snippet_id":"x","x":0,"y":0,"surprise":true}]""");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // `id` and `snippet_id` are the identity of a node; without either the
    // executor cannot resolve what to run.
    [Theory]
    [InlineData("""[{"snippet_id":"x","x":0,"y":0}]""")]
    [InlineData("""[{"id":"a","x":0,"y":0}]""")]
    public void Schema_AMissingRequiredNodeFieldIsRejected(string nodes)
    {
        Assert.False(Validate(nodes).IsValid);
    }

    // Coordinates are editor chrome, not semantics, so a node without them
    // still validates — an API-created workflow needn't carry a layout.
    [Fact]
    public void Schema_CoordinatesAreOptional()
    {
        Assert.True(Validate("""[{"id":"a","snippet_id":"x"}]""").IsValid);
    }

    [Fact]
    public void Schema_AnOutOfEnumNodeTypeIsRejected()
    {
        Assert.False(Validate("[" + Node(type: "sentinel") + "]").IsValid);
    }

    [Fact]
    public void Schema_AnEdgeWithoutASourceIsRejected()
    {
        Assert.False(Validate("[]", """[{"target":"b","type":"success"}]""").IsValid);
    }

    [Fact]
    public void Schema_AnOutOfEnumEdgeTypeIsRejected()
    {
        Assert.False(Validate("[]", """[{"source":"a","target":"b","type":"teleport"}]""").IsValid);
    }

    // A rejection has to say something — an empty error list would leave the
    // wizard's "commit failed" toast blank.
    [Fact]
    public void Schema_ARejectionAlwaysCarriesAtLeastOneMessage()
    {
        var result = Validate("""[{"nonsense":1}]""");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e)));
    }

    [Fact]
    public void Schema_TheValidatorAdvertisesTheV1Version()
    {
        var validator = SharedSchemaValidator.Instance;

        Assert.Equal("v1", validator.CurrentSchemaVersion);
        Assert.NotEmpty(validator.RawJson);
    }

    // ─── CredentialEncryptionService ────────────────────────────────────

    private static CredentialEncryptionService Crypto()
        => new(DataProtectionProvider.Create("flow-weaver-tests"),
               NullLogger<CredentialEncryptionService>.Instance);

    [Fact]
    public void Crypto_RoundTripsAValue()
    {
        var crypto = Crypto();

        var cipher = crypto.Encrypt("hunter2");

        Assert.NotNull(cipher);
        Assert.Equal("hunter2", crypto.Decrypt(cipher));
    }

    // The ciphertext must not contain the plaintext — the whole point of the
    // column.
    [Fact]
    public void Crypto_TheCiphertextDoesNotContainThePlaintext()
    {
        var cipher = Crypto().Encrypt("hunter2")!;

        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(cipher));
    }

    // Data protection is randomised, so encrypting the same value twice must
    // not produce identical bytes — otherwise equal ciphertexts would leak
    // that two credentials share a password.
    [Fact]
    public void Crypto_EncryptingTheSameValueTwiceYieldsDifferentCiphertext()
    {
        var crypto = Crypto();

        Assert.NotEqual(crypto.Encrypt("hunter2"), crypto.Encrypt("hunter2"));
    }

    // Null in / null out is what makes the three-state update semantics
    // ("leave alone") work throughout the services.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Crypto_AnAbsentValueEncryptsToNull(string? plaintext)
    {
        Assert.Null(Crypto().Encrypt(plaintext));
    }

    [Fact]
    public void Crypto_AnAbsentCiphertextDecryptsToNull()
    {
        var crypto = Crypto();

        Assert.Null(crypto.Decrypt(null));
        Assert.Null(crypto.Decrypt(Array.Empty<byte>()));
    }

    [Fact]
    public void Crypto_UnicodeSurvivesTheRoundTrip()
    {
        var crypto = Crypto();
        const string secret = "contraseña-ñ-日本語-🔑";

        Assert.Equal(secret, crypto.Decrypt(crypto.Encrypt(secret)));
    }

    // Tampered or foreign ciphertext must fail loudly rather than yield
    // garbage a handler would then send to a device.
    [Fact]
    public void Crypto_TamperedCiphertextThrows()
    {
        var crypto = Crypto();
        var cipher = crypto.Encrypt("hunter2")!;
        cipher[^1] ^= 0xFF;

        Assert.ThrowsAny<Exception>(() => crypto.Decrypt(cipher));
    }

    [Fact]
    public void Crypto_CiphertextFromAnotherProtectorThrows()
    {
        var cipher = new CredentialEncryptionService(
            DataProtectionProvider.Create("other-app"),
            NullLogger<CredentialEncryptionService>.Instance).Encrypt("hunter2")!;

        Assert.ThrowsAny<Exception>(() => Crypto().Decrypt(cipher));
    }

    // ─── IntegrationActionService ───────────────────────────────────────

    private sealed class ActionFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public IntegrationActionService Build() => new(
            new RepositoryBase<IntegrationActionModel>(Db),
            new IntegrationRepository(Db),
            new FakeUser(),
            NullLogger<IntegrationActionService>.Instance);

        public Guid SeedIntegration(bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "Slack",
                Type = "generic_rest",
                BaseURL = "https://slack.test",
                AuthConfig = TestJson.Element("{}"),
                Headers = TestJson.Element("{}"),
                HealthCheck = TestJson.Element("{}"),
                Status = IntegrationStatus.Healthy,
                Enabled = true,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedAction(Guid integrationId, string name = "post_message")
        {
            var id = Guid.NewGuid();
            Db.IntegrationActions.Add(new IntegrationActionModel
            {
                IntegrationActionId = id,
                IntegrationId = integrationId,
                Name = name,
                Method = "POST",
                Path = "/chat.postMessage",
                IsActive = true,
                Enabled = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string ErrorOf<T>(ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    private static CreateIntegrationAction NewAction(
        string name = "post_message", string method = "post", string path = "/chat.postMessage")
        => new() { Name = name, Method = method, Path = path };

    [Theory]
    [InlineData("", "POST", "/x", "name is required")]
    [InlineData("a", "", "/x", "method is required")]
    [InlineData("a", "POST", "", "path is required")]
    public async Task Action_CreateRequiresTheThreeCoreFields(
        string name, string method, string path, string expected)
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();

        var result = await f.Build().PostForIntegrationAsync(
            integrationId, NewAction(name, method, path));

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(f.Db.IntegrationActions);
    }

    // An action must hang off an integration that exists — otherwise a caller
    // could attach actions to a parent that isn't there.
    [Fact]
    public async Task Action_CreateOnAnUnknownIntegrationIs404()
    {
        using var f = new ActionFixture();

        var result = await f.Build().PostForIntegrationAsync(Guid.NewGuid(), NewAction());

        Assert.Equal("integration not found", ErrorOf(result));
        Assert.Empty(f.Db.IntegrationActions);
    }

    // The verb is normalised so the executor never has to case-fold it.
    [Fact]
    public async Task Action_TheMethodIsUppercased()
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();

        var result = await f.Build().PostForIntegrationAsync(
            integrationId, NewAction(method: "patch"));

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal("PATCH", f.Db.IntegrationActions.Single().Method);
    }

    [Fact]
    public async Task Action_CreatePersistsUnderTheParent()
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();

        await f.Build().PostForIntegrationAsync(integrationId, NewAction());

        var saved = Assert.Single(f.Db.IntegrationActions);
        Assert.Equal(integrationId, saved.IntegrationId);
        Assert.True(saved.Enabled);
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task Action_UpdatingAnUnknownIdIs404()
    {
        using var f = new ActionFixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateIntegrationAction { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Action_OmittedFieldsAreLeftUntouched()
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();
        var id = f.SeedAction(integrationId);

        await f.Build().UpdateAsync(id, new UpdateIntegrationAction { Description = "sends a message" });

        var saved = f.Db.IntegrationActions.Single();
        Assert.Equal("post_message", saved.Name);
        Assert.Equal("/chat.postMessage", saved.Path);
        Assert.Equal("sends a message", saved.Description);
    }

    [Fact]
    public async Task Action_DeleteIsSoft()
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();
        var id = f.SeedAction(integrationId);

        await f.Build().DeleteAsync(id);

        Assert.False(f.Db.IntegrationActions.Single().IsActive);
    }

    [Fact]
    public async Task Action_DeletingAnUnknownIdIs404()
    {
        using var f = new ActionFixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().DeleteAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Action_GetByIdResolvesAStoredActionAndNothingElse()
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();
        var mine = f.SeedAction(integrationId, "mine");

        Assert.NotNull((await f.Build().GetByIdAsync(mine)).Value
            ?? (Assert.IsAssignableFrom<ObjectResult>((await f.Build().GetByIdAsync(mine)).Result).Value
                as IntegrationActionResponse));
        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Action_ListReturnsTheStoredActions()
    {
        using var f = new ActionFixture();
        var integrationId = f.SeedIntegration();
        f.SeedAction(integrationId, "mine");
        f.SeedAction(integrationId, "another");

        var result = await f.Build().GetAsync();

        var page = Assert.IsType<ListResponse<IntegrationActionResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(2, page.Total);
    }
}

