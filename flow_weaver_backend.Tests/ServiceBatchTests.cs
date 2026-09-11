using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Compiler;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// Three services that each guard a different boundary: the chat-to-web account
// link (a single-use token that must not leak whether it exists), the export /
// import round trip (format dispatch + refusal), and the integration health
// probe (whose leniency default exists because most internal services answer
// 404 on `/`).
public class ServiceBatchTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // ─── MessagingLinkService ───────────────────────────────────────────

    // The single-use consume is a set-based UPDATE, which the InMemory
    // provider doesn't implement. Overriding just that statement keeps the
    // rest of the repository (and therefore the atomicity contract the
    // service relies on) exercised as written.
    private sealed class InMemoryLinkTokens : MessagingLinkTokenRepository
    {
        private readonly AppDbContext _db;
        public InMemoryLinkTokens(AppDbContext db) : base(db) => _db = db;

        public override async Task<bool> TryConsumeAsync(
            Guid tokenId, Guid consumedByUserId, DateTime nowUtc, CancellationToken ct = default)
        {
            var row = await _db.MessagingLinkTokens
                .FirstOrDefaultAsync(t => t.MessagingLinkTokenId == tokenId && t.ConsumedAt == null, ct);
            if (row is null) return false;
            row.ConsumedAt = nowUtc;
            row.ConsumedByUserId = consumedByUserId;
            row.UpdatedAt = nowUtc;
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }

    private sealed class LinkFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public MessagingOptions Options { get; set; } = new();
        public FakeUser Caller { get; set; } = new() { UserId = User };

        public MessagingLinkService Build() => new(
            new InMemoryLinkTokens(Db),
            new MessagingChannelRepository(Db),
            new MessagingIdentityLinkRepository(Db),
            Caller,
            Wrap(Options));

        public MessagingChannel SeedChannel()
        {
            var channel = new MessagingChannel
            {
                MessagingChannelId = Guid.NewGuid(),
                Name = "ops-room",
                Provider = "telegram",
                Enabled = true,
                IsActive = true,
            };
            Db.MessagingChannels.Add(channel);
            Db.SaveChanges();
            return channel;
        }

        public void Dispose() => Db.Dispose();
    }

    private static IOptions<MessagingOptions> Wrap(MessagingOptions o) => new Wrapper<MessagingOptions>(o);

    private sealed class Wrapper<T> : IOptions<T> where T : class
    {
        public Wrapper(T value) => Value = value;
        public T Value { get; }
    }

    [Fact]
    public async Task Link_CreateIssuesAOneTimeTokenAndAnOpenableUrl()
    {
        using var f = new LinkFixture
        {
            Options = new MessagingOptions { PublicBaseUrl = "https://app.test/" },
        };
        var channel = f.SeedChannel();

        var invite = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);

        Assert.Equal($"https://app.test/link?token={invite.Cleartext}", invite.Url);
        // The cleartext is 32 random bytes hex-encoded.
        Assert.Equal(64, invite.Cleartext.Length);

        var stored = Assert.Single(f.Db.MessagingLinkTokens);
        Assert.Equal("U9", stored.ExternalUserId);
        Assert.Equal(channel.MessagingChannelId, stored.MessagingChannelId);
        // Only the hash is persisted — a DB leak must not yield usable tokens.
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(invite.Cleartext)),
            stored.TokenHash);
    }

    // Without a configured public base URL there is no openable link, so the
    // caller gets an empty Url and can degrade to "link from the web app"
    // instead of DMing something broken.
    [Fact]
    public async Task Link_WithoutAPublicBaseUrlTheInviteCarriesNoUrl()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();

        var invite = await f.Build().CreateLinkAsync(channel, null, "U9", default);

        Assert.Equal(string.Empty, invite.Url);
        Assert.NotEmpty(invite.Cleartext);
    }

    [Fact]
    public async Task Link_AnAbsentWorkspaceIsStoredAsTheEmptyString()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();

        await f.Build().CreateLinkAsync(channel, null, "U9", default);

        Assert.Equal(string.Empty, f.Db.MessagingLinkTokens.Single().ExternalWorkspaceId);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(-5, 15)]
    [InlineData(60, 60)]
    public async Task Link_TheTtlFallsBackTo15MinutesWhenMisconfigured(int configured, int expectedMinutes)
    {
        using var f = new LinkFixture
        {
            Options = new MessagingOptions { LinkTokenTtlMinutes = configured },
        };
        var channel = f.SeedChannel();

        var before = DateTime.UtcNow;
        await f.Build().CreateLinkAsync(channel, null, "U9", default);

        var expiry = f.Db.MessagingLinkTokens.Single().ExpiresAt;
        Assert.InRange(expiry, before.AddMinutes(expectedMinutes - 1), DateTime.UtcNow.AddMinutes(expectedMinutes + 1));
    }

    // Every rejection path looks the same from outside: a wrong, expired or
    // foreign token must not reveal which tokens exist.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("deadbeef")]
    public async Task Link_APreviewOfAnUnusableTokenIsIndistinguishable(string token)
    {
        using var f = new LinkFixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().PreviewAsync(token));
    }

    [Fact]
    public async Task Link_PreviewReturnsTheChannelContextForAValidToken()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();
        var invite = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);

        var result = await f.Build().PreviewAsync(invite.Cleartext);

        var body = Assert.IsType<MessagingLinkPreviewResponse>(result.Value);
        Assert.Equal("telegram", body.Provider);
        Assert.Equal("ops-room", body.ChannelName);
        Assert.Equal("U9", body.ExternalUserId);
    }

    [Fact]
    public async Task Link_AnExpiredTokenIsNotResolvable()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();
        var invite = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);
        var stored = f.Db.MessagingLinkTokens.Single();
        stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        f.Db.SaveChanges();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().PreviewAsync(invite.Cleartext));
    }

    [Fact]
    public async Task Link_ConfirmCreatesTheIdentityLinkForTheCurrentUser()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();
        var invite = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);

        var result = await f.Build().ConfirmAsync(invite.Cleartext);

        var body = Assert.IsType<MessagingLinkConfirmResponse>(result.Value);
        Assert.True(body.Linked);
        var link = Assert.Single(f.Db.MessagingIdentityLinks);
        Assert.Equal(User, link.LinkedUserId);
        Assert.Equal("U9", link.ExternalUserId);
        Assert.Equal(body.MessagingIdentityLinkId, link.MessagingIdentityLinkId);
    }

    // Single-use: the second confirm of the same token has nothing left to
    // consume.
    [Fact]
    public async Task Link_ATokenCannotBeConfirmedTwice()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();
        var invite = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);
        await f.Build().ConfirmAsync(invite.Cleartext);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().ConfirmAsync(invite.Cleartext));
        Assert.Single(f.Db.MessagingIdentityLinks);
    }

    // Re-linking an external identity to a different user updates the existing
    // row rather than violating the unique (channel, workspace, user) index.
    [Fact]
    public async Task Link_RelinkingAnExternalIdentityRebindsTheExistingRow()
    {
        using var f = new LinkFixture();
        var channel = f.SeedChannel();
        var first = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);
        await f.Build().ConfirmAsync(first.Cleartext);

        var second = await f.Build().CreateLinkAsync(channel, "W1", "U9", default);
        var otherUser = Guid.NewGuid();
        f.Caller = new FakeUser { UserId = otherUser };
        await f.Build().ConfirmAsync(second.Cleartext);

        var link = Assert.Single(f.Db.MessagingIdentityLinks);
        Assert.Equal(otherUser, link.LinkedUserId);
    }

    // ─── WorkflowExportService ──────────────────────────────────────────

    private sealed class StubYamlCompiler : IWorkflowYamlCompiler
    {
        public string Compile(WorkflowModel workflow) => $"name: {workflow.Name}\n";
    }

    private sealed class RecordingExporter : IWorkflowExporter
    {
        public RecordingExporter(string format) => Format = format;
        public string Format { get; }
        public string ContentType => "text/plain";
        public string FileExtension => "txt";
        public IReadOnlyDictionary<Guid, SnippetModel>? LastSnippets { get; private set; }

        public Task<string> ExportAsync(
            WorkflowModel workflow, IReadOnlyDictionary<Guid, SnippetModel> snippets, CancellationToken ct)
        {
            LastSnippets = snippets;
            return Task.FromResult("exported");
        }
    }

    private sealed class ExportFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public List<IWorkflowExporter> Exporters { get; } = new();

        public WorkflowExportService Build() => new(
            new RepositoryBase<WorkflowModel>(Db),
            new SnippetRepository(Db),
            new StubYamlCompiler(),
            Exporters);

        public Guid SeedWorkflow(string name = "lldp sync", string nodes = "[]")
        {
            var id = Guid.NewGuid();
            Db.Workflows.Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = name,
                Description = "d",
                Environment = "draft",
                SchemaVersion = "v1",
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element("[]"),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedSnippet()
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id, Name = "s", Type = "ssh", IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("yml")]
    [InlineData("YAML")]
    public async Task Export_YamlUsesTheCompilerAndAYamlFilename(string format)
    {
        using var f = new ExportFixture();
        var id = f.SeedWorkflow();

        var payload = await f.Build().ExportAsync(id, format, default);

        Assert.Equal("name: lldp sync\n", payload.Content);
        Assert.Equal("application/x-yaml", payload.ContentType);
        // Spaces in the workflow name can't ride into a Content-Disposition
        // filename unescaped.
        Assert.DoesNotContain(" ", payload.Filename);
        Assert.EndsWith(".yaml", payload.Filename);
    }

    [Fact]
    public async Task Export_JsonCarriesTheGraphAndTheSchemaVersion()
    {
        using var f = new ExportFixture();
        var id = f.SeedWorkflow(nodes: """[{"id":"a"}]""");

        var payload = await f.Build().ExportAsync(id, "json", default);

        using var doc = JsonDocument.Parse(payload.Content);
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("v1", doc.RootElement.GetProperty("workflow").GetProperty("SchemaVersion").GetString());
        Assert.Equal("a", doc.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("id").GetString());
        Assert.Equal("application/json", payload.ContentType);
    }

    // A registered exporter is dispatched by name and handed the resolved
    // snippet bodies up front, so it never re-queries per node.
    [Fact]
    public async Task Export_ARegisteredExporterReceivesTheReferencedSnippets()
    {
        using var f = new ExportFixture();
        var exporter = new RecordingExporter("python");
        f.Exporters.Add(exporter);
        var snippetId = f.SeedSnippet();
        var id = f.SeedWorkflow(nodes: "[{\"id\":\"a\",\"snippet_id\":\"" + snippetId + "\"}]");

        var payload = await f.Build().ExportAsync(id, "python", default);

        Assert.Equal("exported", payload.Content);
        Assert.EndsWith(".txt", payload.Filename);
        Assert.Contains(snippetId, exporter.LastSnippets!.Keys);
    }

    // The refusal names every format the caller could have used, so the error
    // is self-service.
    [Fact]
    public async Task Export_AnUnknownFormatListsTheSupportedOnes()
    {
        using var f = new ExportFixture();
        f.Exporters.Add(new RecordingExporter("python"));
        var id = f.SeedWorkflow();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().ExportAsync(id, "cobol", default));

        Assert.Contains("yaml", ex.Message);
        Assert.Contains("json", ex.Message);
        Assert.Contains("python", ex.Message);
    }

    [Fact]
    public async Task Export_AnUnknownWorkflowIsNotFound()
    {
        using var f = new ExportFixture();

        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Build().ExportAsync(Guid.NewGuid(), "json", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseImport_AnEmptyBodyIsRejected(string raw)
    {
        using var f = new ExportFixture();

        var ex = Assert.Throws<ValidationException>(() => f.Build().ParseImport(raw, "json"));

        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void ParseImport_ReadsAJsonWorkflow()
    {
        using var f = new ExportFixture();

        var payload = f.Build().ParseImport(
            """{"workflow":{"name":"imported"},"nodes":[],"edges":[]}""", "json");

        Assert.Equal("imported", payload.Name);
    }

    [Fact]
    public void ParseImport_ReadsAYamlWorkflow()
    {
        using var f = new ExportFixture();

        var payload = f.Build().ParseImport("workflow:\n  name: imported\nnodes: []\nedges: []\n", "yaml");

        Assert.Equal("imported", payload.Name);
    }

    [Fact]
    public void ParseImport_AnUnsupportedFormatIsRejected()
    {
        using var f = new ExportFixture();

        var ex = Assert.Throws<ValidationException>(() => f.Build().ParseImport("x", "xml"));

        Assert.Contains("unsupported import format", ex.Message);
    }

    // Malformed input becomes a validation error the API can render, not a
    // raw parser exception.
    [Fact]
    public void ParseImport_MalformedJsonBecomesAValidationError()
    {
        using var f = new ExportFixture();

        var ex = Assert.Throws<ValidationException>(() => f.Build().ParseImport("{ broken", "json"));

        Assert.Contains("failed to parse", ex.Message);
    }

    [Fact]
    public void ParseImport_AWorkflowWithoutANameIsRejected()
    {
        using var f = new ExportFixture();

        var ex = Assert.Throws<ValidationException>(
            () => f.Build().ParseImport("""{"workflow":{},"nodes":[],"edges":[]}""", "json"));

        Assert.Contains("name is required", ex.Message);
    }

    // ─── IntegrationHealthChecker ───────────────────────────────────────

    private sealed class HealthFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeHttpMessageHandler Http { get; set; } = new(HttpStatusCode.OK, "ok");

        public IntegrationHealthChecker Build() => new(
            new IntegrationRepository(Db),
            new IntegrationActionRepository(Db),
            new FakeUser(),
            new FakeHttpClientFactory(Http),
            TestAuth.Applier(),
            NullLogger<IntegrationHealthChecker>.Instance);

        public Guid Seed(string baseUrl = "https://api.test", string healthCheck = "{}")
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "Jira",
                Type = "generic_rest",
                BaseURL = baseUrl,
                AuthConfig = TestJson.Element("{}"),
                Headers = TestJson.Element("{}"),
                HealthCheck = TestJson.Element(healthCheck),
                Status = IntegrationStatus.NeedsConfig,
                Enabled = true,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static IntegrationHealthCheckResult Health(ActionResult<IntegrationHealthCheckResult> result)
        => Assert.IsType<IntegrationHealthCheckResult>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task Health_AnUnknownIntegrationIs404()
    {
        using var f = new HealthFixture();

        var result = await f.Build().CheckAsync(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // Lenient default: most internal REST services answer 404 on `/` and are
    // perfectly operational, so "the service answered" is the bar. 401/403
    // are deliberately NOT here anymore — an auth rejection means every real
    // call would fail too (see Health_A401IsUnhealthyEvenInLenientMode).
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Health_WithNoConfiguredCheckAnyNon5xxCountsAsHealthy(HttpStatusCode status)
    {
        using var f = new HealthFixture { Http = new FakeHttpMessageHandler(status, "") };
        var id = f.Seed();

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("healthy", body.Status);
        Assert.Equal((int)status, body.StatusCode);
    }

    // A 401 was previously "healthy" under leniency — a false positive. But
    // without credentials configured it's also not provably broken (the
    // integration may only use open endpoints), so it lands on "degraded"
    // with guidance. With credentials it's a hard unhealthy (see
    // Integration_Tests.Lenient_403_with_credentials_is_unhealthy).
    [Fact]
    public async Task Health_A401WithoutCredentialsIsDegraded()
    {
        using var f = new HealthFixture { Http = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "") };
        var id = f.Seed();

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("degraded", body.Status);
        Assert.Contains("requires authentication", body.Error);
    }

    // 5xx is the one case leniency still flags — the server is up in the TCP
    // sense but returning errors.
    [Fact]
    public async Task Health_A5xxIsUnhealthyEvenInLenientMode()
    {
        using var f = new HealthFixture
        {
            Http = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, ""),
        };
        var id = f.Seed();

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("unhealthy", body.Status);
        Assert.Contains("500", body.Error);
    }

    // Once the admin configures a health check, their expected status is
    // honoured exactly.
    [Fact]
    public async Task Health_AConfiguredCheckIsStrictAboutTheStatusCode()
    {
        using var f = new HealthFixture { Http = new FakeHttpMessageHandler(HttpStatusCode.NotFound, "") };
        var id = f.Seed(healthCheck: """{"path":"/healthz","expected_status":200}""");

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("unhealthy", body.Status);
        Assert.Contains("unexpected status code 404", body.Error);
        Assert.Equal(200, body.Expected);
    }

    [Fact]
    public async Task Health_AConfiguredCheckThatMatchesIsHealthy()
    {
        using var f = new HealthFixture { Http = new FakeHttpMessageHandler(HttpStatusCode.NoContent, "") };
        var id = f.Seed(healthCheck: """{"path":"/healthz","expected_status":204}""");

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("healthy", body.Status);
    }

    [Fact]
    public async Task Health_AnIntegrationWithoutABaseUrlIsUnhealthy()
    {
        using var f = new HealthFixture();
        var id = f.Seed(baseUrl: "");

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("unhealthy", body.Status);
        Assert.Contains("base_url is not set", body.Error);
    }

    [Fact]
    public async Task Health_AnUnparseableUrlIsUnhealthy()
    {
        using var f = new HealthFixture();
        var id = f.Seed(baseUrl: "not a url");

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("unhealthy", body.Status);
        Assert.Contains("not a valid absolute URL", body.Error);
    }

    [Fact]
    public async Task Health_ATransportFailureIsUnhealthy()
    {
        using var f = new HealthFixture
        {
            Http = new FakeHttpMessageHandler(_ => throw new HttpRequestException("no route to host")),
        };
        var id = f.Seed();

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("unhealthy", body.Status);
        Assert.Contains("no route", body.Error);
    }

    [Fact]
    public async Task Health_ATimeoutIsUnhealthy()
    {
        using var f = new HealthFixture
        {
            Http = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("timeout")),
        };
        var id = f.Seed();

        var body = Health(await f.Build().CheckAsync(id, default));

        Assert.Equal("unhealthy", body.Status);
        Assert.Equal("request timed out", body.Error);
    }

    // The probe result is persisted so the Integrations list can show it
    // without re-probing on every refresh.
    [Fact]
    public async Task Health_TheOutcomeIsPersistedOnTheIntegrationRow()
    {
        using var f = new HealthFixture();
        var id = f.Seed();

        await f.Build().CheckAsync(id, default);

        var row = f.Db.Integrations.Single();
        Assert.Equal("healthy", row.Status);
        Assert.NotNull(row.LastCheckedAt);
    }

    [Fact]
    public async Task Health_TheConfiguredPathIsAppendedToTheBaseUrl()
    {
        using var f = new HealthFixture();
        var id = f.Seed(baseUrl: "https://api.test/", healthCheck: """{"path":"healthz","expected_status":200}""");

        await f.Build().CheckAsync(id, default);

        Assert.Equal("https://api.test/healthz", f.Http.Requests[^1].RequestUri!.AbsoluteUri);
    }
}


