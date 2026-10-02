using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AiAgentModel = flow_weaver_backend.Models.AIAgent;
using SecretModel = flow_weaver_backend.Models.Secret;

namespace flow_weaver_backend.Tests;

// Three admin-facing controllers. Two themes run through them: a settings
// change that alters how the whole app authorises has to leave an audit
// trail (and only when it actually changed), and a secret's plaintext must
// never leave the process — not in a response, not in an audit payload.
public class AdminControllerBatchTests
{

    private sealed class RecordingAudit : flow_weaver_backend.Services.Audit.IAuditLogger
    {
        public List<(string Action, string Payload)> Entries { get; } = new();
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Entries.Add((action,
                JsonSerializer.Serialize(new { before, after })));
            return Task.CompletedTask;
        }
        public List<string> Actions => Entries.Select(e => e.Action).ToList();
    }

    private static ControllerContext Context()
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "22222222-2222-2222-2222-222222222222"),
                }, "test")),
            },
        };

    // ─── AdminSettingsController ────────────────────────────────────────

    // An in-memory settings store: Put reads the previous value, writes the
    // new one, and the controller's audit decisions hinge on the delta.
    private sealed class MemorySettings : IAppSettingsService
    {
        public AppSettings Current { get; set; } = AppSettings.Default;
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(Current);
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
        {
            Current = updated;
            return Task.FromResult(updated);
        }
    }

    private static (AdminSettingsController Controller, MemorySettings Settings, RecordingAudit Audit)
        BuildSettings(AppSettings? initial = null)
    {
        var settings = new MemorySettings();
        if (initial is not null) settings.Current = initial;
        var audit = new RecordingAudit();
        var controller = new AdminSettingsController(
            settings,
            new FakeUser(),
            audit,
            NullLogger<AdminSettingsController>.Instance)
        { ControllerContext = Context() };
        return (controller, settings, audit);
    }

    private static AppSettingsRequest Request(
        bool granular = false, double threshold = 0.8, double gap = 0.1, string rbac = "legacy")
        => new()
        {
            PermissionsGranularGatingEnabled = granular,
            ImportFuzzyMatchThreshold = threshold,
            ImportFuzzyMatchGap = gap,
            RbacMode = rbac,
        };

    private static AppSettingsResponse Body(ActionResult<AppSettingsResponse> result)
        => Assert.IsType<AppSettingsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task Settings_GetReturnsTheStoredValues()
    {
        var (controller, settings, _) = BuildSettings(AppSettings.Default with
        {
            PermissionsGranularGatingEnabled = true,
            RbacMode = "granular",
        });

        var body = Body(await controller.Get(default));

        Assert.True(body.PermissionsGranularGatingEnabled);
        Assert.Equal("granular", body.RbacMode);
        Assert.Equal(settings.Current.ImportFuzzyMatchThreshold, body.ImportFuzzyMatchThreshold);
    }

    [Fact]
    public async Task Settings_PutPersistsAndEchoesTheNewValues()
    {
        var (controller, settings, _) = BuildSettings();

        var body = Body(await controller.Put(
            Request(granular: true, threshold: 0.6, gap: 0.2, rbac: "granular"), default));

        Assert.True(body.PermissionsGranularGatingEnabled);
        Assert.Equal(0.6, body.ImportFuzzyMatchThreshold);
        Assert.Equal("granular", settings.Current.RbacMode);
    }

    // The RBAC mode switch changes how every migrated endpoint authorises,
    // so it gets its own audit action.
    [Fact]
    public async Task Settings_ChangingTheRbacModeIsAudited()
    {
        var (controller, _, audit) = BuildSettings();

        await controller.Put(Request(rbac: "granular"), default);

        Assert.Contains("app_settings.rbac_mode.changed", audit.Actions);
    }

    [Fact]
    public async Task Settings_ToggleAuditsNameTheDirection()
    {
        var (controller, _, audit) = BuildSettings();

        await controller.Put(Request(granular: true), default);
        Assert.Contains("app_settings.granular_gating.enabled", audit.Actions);

        await controller.Put(Request(granular: false), default);
        Assert.Contains("app_settings.granular_gating.disabled", audit.Actions);
    }

    [Fact]
    public async Task Settings_ChangingTheFuzzyMatcherIsAudited()
    {
        var (controller, _, audit) = BuildSettings();

        await controller.Put(Request(threshold: 0.55), default);

        Assert.Contains("app_settings.import_fuzzy_match.updated", audit.Actions);
    }

    [Fact]
    public async Task Settings_ChangingOnlyTheGapIsStillAudited()
    {
        var (controller, _, audit) = BuildSettings();

        await controller.Put(Request(gap: 0.42), default);

        Assert.Contains("app_settings.import_fuzzy_match.updated", audit.Actions);
    }

    // Re-saving the form unchanged must not pollute /admin/audit.
    [Fact]
    public async Task Settings_AnUnchangedSaveWritesNoAuditRows()
    {
        var (controller, _, audit) = BuildSettings(AppSettings.Default with
        {
            PermissionsGranularGatingEnabled = true,
            ImportFuzzyMatchThreshold = 0.8,
            ImportFuzzyMatchGap = 0.1,
            RbacMode = "granular",
        });

        await controller.Put(
            Request(granular: true, threshold: 0.8, gap: 0.1, rbac: "granular"), default);

        Assert.Empty(audit.Actions);
    }

    // Float comparison uses a tolerance, so a value that round-trips through
    // JSON must not read as "changed".
    [Fact]
    public async Task Settings_AFloatThatRoundTripsIdenticallyIsNotAChange()
    {
        var (controller, _, audit) = BuildSettings(AppSettings.Default with
        {
            ImportFuzzyMatchThreshold = 0.8,
            ImportFuzzyMatchGap = 0.1,
        });

        await controller.Put(Request(threshold: 0.8 + 1e-12, gap: 0.1), default);

        Assert.DoesNotContain("app_settings.import_fuzzy_match.updated", audit.Actions);
    }

    // ─── SecretsController ──────────────────────────────────────────────

    private sealed class SecretsFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingAudit Audit { get; } = new();

        public SecretsController Build()
            => new(Db, new FakeUser (), new FakeCrypto(), Audit, new FakeTrace())
            { ControllerContext = Context() };

        public Guid Seed(string name = "netbox-token", string value = "s3cret", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Secrets.Add(new SecretModel
            {
                SecretId = id,
                Name = name,
                Description = "token for netbox",
                EncryptedValue = Encoding.UTF8.GetBytes(value),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static SecretResponse SecretBody(ActionResult<SecretResponse> result)
        => Assert.IsType<SecretResponse>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    [Fact]
    public async Task Secrets_ListOnlyReturnsTheActiveRows()
    {
        using var f = new SecretsFixture();
        f.Seed("a-token");
        f.Seed("deleted-token", active: false);

        var result = await f.Build().List(default);

        var rows = Assert.IsAssignableFrom<IEnumerable<SecretResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("a-token", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task Secrets_GetReturnsTheRowWithoutItsValue()
    {
        using var f = new SecretsFixture();
        var id = f.Seed();

        var body = SecretBody(await f.Build().Get(id, default));

        Assert.Equal("netbox-token", body.Name);
        // The plaintext must never leave the process — the response type has
        // no slot for it at all.
        Assert.Null(typeof(SecretResponse).GetProperty("Value"));
    }

    [Fact]
    public async Task Secrets_GetOnAnUnknownIdIs404()
    {
        using var f = new SecretsFixture();

        Assert.IsType<NotFoundResult>((await f.Build().Get(Guid.NewGuid(), default)).Result);
    }

    [Fact]
    public async Task Secrets_GetOnASoftDeletedRowIs404()
    {
        using var f = new SecretsFixture();
        var id = f.Seed(active: false);

        Assert.IsType<NotFoundResult>((await f.Build().Get(id, default)).Result);
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("Has Spaces", "v")]
    [InlineData("UPPERCASE", "v")]
    [InlineData("ok-name", "")]      // value required
    public async Task Secrets_CreateValidatesNameAndValue(string name, string value)
    {
        using var f = new SecretsFixture();

        var result = await f.Build().Create(
            new CreateSecretRequest { Name = name, Value = value }, default);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(f.Db.Secrets);
    }

    // Names are 2–64 chars, matching the rejection message.
    [Theory]
    [InlineData("a", false)]
    [InlineData("db", true)]
    [InlineData("-db", false)]
    [InlineData("netbox-token", true)]
    public async Task Secrets_NameLengthMatchesTheMessage(string name, bool accepted)
    {
        using var f = new SecretsFixture();

        var result = await f.Build().Create(
            new CreateSecretRequest { Name = name, Value = "v" }, default);

        if (accepted) Assert.IsType<CreatedAtActionResult>(result.Result);
        else Assert.IsNotType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Secrets_CreatePersistsAnEncryptedValueAndAuditsWithoutIt()
    {
        using var f = new SecretsFixture();

        var result = await f.Build().Create(
            new CreateSecretRequest { Name = "netbox-token", Value = "s3cret", Description = " token " },
            default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(f.Db.Secrets);
        Assert.Equal("token", saved.Description);
        Assert.NotEmpty(saved.EncryptedValue);
        // The audit payload records that a secret was created, never what
        // it contains.
        Assert.DoesNotContain("s3cret", Assert.Single(f.Audit.Entries).Payload);
    }

    [Fact]
    public async Task Secrets_CreatingADuplicateNameIs409()
    {
        using var f = new SecretsFixture();
        f.Seed("netbox-token");

        var result = await f.Build().Create(
            new CreateSecretRequest { Name = "netbox-token", Value = "x" }, default);

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
    }

    // A soft-deleted name is still taken: re-creating it would make audit
    // history ambiguous between two SecretIds.
    [Fact]
    public async Task Secrets_ASoftDeletedNameIsStillTaken()
    {
        using var f = new SecretsFixture();
        f.Seed("netbox-token", active: false);

        var result = await f.Build().Create(
            new CreateSecretRequest { Name = "netbox-token", Value = "x" }, default);

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
    }

    // Three-state update semantics: null value = leave the ciphertext alone.
    [Fact]
    public async Task Secrets_UpdatingOnlyTheDescriptionKeepsTheCiphertext()
    {
        using var f = new SecretsFixture();
        var id = f.Seed(value: "original");

        await f.Build().Update(id, new UpdateSecretRequest { Description = "new note" }, default);

        var saved = f.Db.Secrets.Single();
        Assert.Equal("new note", saved.Description);
        Assert.Equal("original", Encoding.UTF8.GetString(saved.EncryptedValue));
    }

    [Fact]
    public async Task Secrets_ANonEmptyValueRotatesTheCiphertext()
    {
        using var f = new SecretsFixture();
        var id = f.Seed(value: "original");

        await f.Build().Update(id, new UpdateSecretRequest { Value = "rotated" }, default);

        Assert.Equal("rotated", Encoding.UTF8.GetString(f.Db.Secrets.Single().EncryptedValue));
    }

    // An empty string would silently blank the credential — the caller is
    // told to delete the secret instead.
    [Fact]
    public async Task Secrets_AnEmptyValueIsRefusedRatherThanBlankingTheSecret()
    {
        using var f = new SecretsFixture();
        var id = f.Seed(value: "original");

        var result = await f.Build().Update(id, new UpdateSecretRequest { Value = "" }, default);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Equal("original", Encoding.UTF8.GetString(f.Db.Secrets.Single().EncryptedValue));
    }

    // The audit records THAT the value rotated, never the value.
    [Fact]
    public async Task Secrets_TheUpdateAuditRecordsRotationWithoutThePlaintext()
    {
        using var f = new SecretsFixture();
        var id = f.Seed();

        await f.Build().Update(id, new UpdateSecretRequest { Value = "rotated" }, default);

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal("update", entry.Action);
        Assert.Contains("value_rotated", entry.Payload);
        Assert.DoesNotContain("hunter2-plaintext", entry.Payload);
    }

    [Fact]
    public async Task Secrets_UpdatingAnUnknownIdIs404()
    {
        using var f = new SecretsFixture();

        var result = await f.Build().Update(
            Guid.NewGuid(), new UpdateSecretRequest { Description = "x" }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // Delete is soft so audit references stay resolvable.
    [Fact]
    public async Task Secrets_DeleteIsSoftAndAudited()
    {
        using var f = new SecretsFixture();
        var id = f.Seed();

        var result = await f.Build().Delete(id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.False(f.Db.Secrets.Single().IsActive);
        Assert.Equal("delete", Assert.Single(f.Audit.Entries).Action);
    }

    [Fact]
    public async Task Secrets_DeletingAnUnknownIdIs404()
    {
        using var f = new SecretsFixture();

        Assert.IsType<NotFoundResult>(await f.Build().Delete(Guid.NewGuid(), default));
    }

    // ─── AIProviderController.Test ──────────────────────────────────────

    // The connectivity check never returns a non-200: the wizard renders the
    // outcome inline, so a broken provider has to come back as
    // `success:false` with the reason rather than an HTTP error.
    private sealed class ProviderFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeUser Caller { get; } = new();
        public FakeHttpMessageHandler Http { get; set; } =
            new(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");

        public AIProviderController Build()
        {
            var factory = new LlmProviderFactory(
                new AiProviderRepository(Db),
                new RepositoryBase<AiAgentModel>(Db),
                Caller,
                new FakeHttpClientFactory(Http),
                new FakeCrypto(),
                NullLoggerFactory.Instance);
            return new AIProviderController(
                new flow_weaver_backend.Services.AIProvider.AIProviderService(
                    new RepositoryBase<AIProvider>(Db), new FakeCrypto(), Caller,
                    new FakeAudit(), NullLogger<flow_weaver_backend.Services.AIProvider.AIProviderService>.Instance),
                factory,
                NullLogger<AIProviderController>.Instance)
            { ControllerContext = Context() };
        }

        public Guid Seed(string model = "gpt-4o", bool enabled = true)
        {
            var id = Guid.NewGuid();
            Db.AIProviders.Add(new AIProvider
            {
                AIProviderId = id,
                Name = "openai-prod",
                Type = "openai",
                BaseURL = "https://api.openai.test",
                EncryptedApiKey = Encoding.UTF8.GetBytes("sk-test"),
                DefaultModel = model,
                Enabled = enabled,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static ProviderTestResponse TestBody(ActionResult<ProviderTestResponse> result)
        => Assert.IsType<ProviderTestResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task ProviderTest_AWorkingProviderReportsSuccessAndTheModel()
    {
        using var f = new ProviderFixture();
        var id = f.Seed(model: "gpt-4o");

        var body = TestBody(await f.Build().Test(id, default));

        Assert.True(body.Success);
        Assert.Equal("gpt-4o", body.Model);
        Assert.Equal("ok", body.Response);
    }

    [Fact]
    public async Task ProviderTest_AnUnknownProviderIsReportedInlineNotAs404()
    {
        using var f = new ProviderFixture();

        var body = TestBody(await f.Build().Test(Guid.NewGuid(), default));

        Assert.False(body.Success);
        Assert.Contains("resolve failed", body.Response);
    }

    [Fact]
    public async Task ProviderTest_AnUpstreamErrorIsReportedInline()
    {
        using var f = new ProviderFixture();
        f.Http = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "bad key");
        var id = f.Seed();

        var body = TestBody(await f.Build().Test(id, default));

        Assert.False(body.Success);
        Assert.NotEmpty(body.Response);
        // The model is still echoed so the user can see what was attempted.
        Assert.Equal("gpt-4o", body.Model);
    }

    [Fact]
    public async Task ProviderTest_ATransportFailureIsReportedInline()
    {
        using var f = new ProviderFixture();
        f.Http = new FakeHttpMessageHandler(_ => throw new HttpRequestException("no route to host"));
        var id = f.Seed();

        var body = TestBody(await f.Build().Test(id, default));

        Assert.False(body.Success);
        Assert.Contains("no route to host", body.Response);
    }

    // A pathological upstream message must not be echoed unbounded into the
    // admin UI.
    [Fact]
    public async Task ProviderTest_TheReportedResponseIsLengthCapped()
    {
        using var f = new ProviderFixture();
        f.Http = new FakeHttpMessageHandler(_ => throw new HttpRequestException(new string('x', 5000)));
        var id = f.Seed();

        var body = TestBody(await f.Build().Test(id, default));

        Assert.True(body.Response.Length <= 420, body.Response.Length.ToString());
    }
}

