using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Tools.Handlers.Git;
using flow_weaver_backend.Services.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Tests;

// The agent tool that creates a GitHub repository and (optionally) registers it
// in FlowWeaver. The behaviour that matters: the PAT never leaves the
// credential store un-decrypted, a GitHub error is surfaced verbatim so the
// agent can relay it, and a FlowWeaver-side registration failure must NOT fail
// the call — the remote repo already exists, and reporting failure would make
// the agent retry and create a second one.
public class GitCreateRemoteRepositoryHandlerTests
{

    private sealed class ScriptedGitService : IGitService
    {
        public CreateGitRepository? CreatedWith { get; private set; }
        public Guid RepositoryId { get; set; } = Guid.NewGuid();
        public ActionResult<GitRepositoryResponse>? FailWith { get; set; }

        public Task<ActionResult<GitRepositoryResponse>> CreateAsync(CreateGitRepository dto, CancellationToken ct)
        {
            CreatedWith = dto;
            if (FailWith is not null) return Task.FromResult(FailWith);
            return Task.FromResult<ActionResult<GitRepositoryResponse>>(
                new GitRepositoryResponse { GitRepositoryId = RepositoryId, Name = dto.Name });
        }

        public Task<ActionResult<ListResponse<GitRepositoryResponse>>> ListAsync(int limit, int offset, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> GetAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> DeleteAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> PullAsync(Guid id, string? branch, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> PushAsync(Guid id, string? branch, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitBranchesResponse>> ListBranchesAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> CheckoutAsync(Guid id, string branch, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitListFilesResponse>> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitReadFileResponse>> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitDiffResponse>> DiffAsync(Guid id, string? from, string? to, string? path, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ScriptedGitService Git { get; } = new();
        public FakeHttpMessageHandler Handler { get; set; } =
            new(HttpStatusCode.Created, GithubRepo());

        public GitCreateRemoteRepositoryHandler Build() => new(
            new RepositoryBase<CredentialModel>(Db),
            new FakeUser(),
            new FakeCrypto(),
            new FakeHttpClientFactory(Handler),
            Git,
            NullLogger<GitCreateRemoteRepositoryHandler>.Instance);

        public Guid SeedCredential(string? pat = "ghp_token", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Credentials.Add(new CredentialModel
            {
                CredentialId = id,
                Name = "github-pat",
                Type = "token",
                EncryptedPassword = pat is null ? null : Encoding.UTF8.GetBytes(pat),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string GithubRepo(
        string fullName = "acme/infra", string cloneUrl = "https://github.com/acme/infra.git",
        string defaultBranch = "main")
        => JsonSerializer.Serialize(new
        {
            full_name = fullName,
            html_url = $"https://github.com/{fullName}",
            clone_url = cloneUrl,
            ssh_url = $"git@github.com:{fullName}.git",
            default_branch = defaultBranch,
        });

    private static JsonElement Args(Guid credentialId, params (string Key, object Value)[] extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = "infra",
            ["auth_credential_id"] = credentialId.ToString(),
        };
        foreach (var (k, v) in extra) payload[k] = v;
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string? ErrorOf(JsonElement result)
        => result.TryGetProperty("error", out var e) ? e.GetString() : null;

    private static async Task<JsonElement> Run(Fixture f, JsonElement args)
        => await f.Build().ExecuteAsync(args, default);

    // ─── argument + credential validation ───────────────────────────────

    [Fact]
    public async Task MissingNameIsAnError()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();
        var args = JsonSerializer.SerializeToElement(new { auth_credential_id = credId.ToString() });

        Assert.Contains("name is required", ErrorOf(await Run(f, args)));
    }

    [Fact]
    public async Task MissingCredentialIdIsAnError()
    {
        using var f = new Fixture();
        var args = JsonSerializer.SerializeToElement(new { name = "infra" });

        Assert.Contains("auth_credential_id is required", ErrorOf(await Run(f, args)));
    }

    [Fact]
    public async Task NonGuidCredentialIdIsAnError()
    {
        using var f = new Fixture();
        var args = JsonSerializer.SerializeToElement(new { name = "infra", auth_credential_id = "not-a-guid" });

        Assert.Contains("must be a UUID", ErrorOf(await Run(f, args)));
    }

    [Fact]
    public async Task UnknownCredentialIsAnError()
    {
        using var f = new Fixture();

        Assert.Contains("not found", ErrorOf(await Run(f, Args(Guid.NewGuid()))));
    }

    [Fact]
    public async Task SoftDeletedCredentialIsNotFound()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential(active: false);

        Assert.Contains("not found", ErrorOf(await Run(f, Args(credId))));
    }

    // The PAT lives in the password field; a credential without one can't
    // authenticate, and the message says exactly where to put it.
    [Fact]
    public async Task CredentialWithoutATokenExplainsWhereToPutIt()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential(pat: null);

        var error = ErrorOf(await Run(f, Args(credId)));

        Assert.Contains("no token in the password field", error);
        Assert.Contains("GitHub PAT", error);
    }

    // ─── the GitHub request ─────────────────────────────────────────────

    private static JsonElement SentBody(Fixture f) => TestJson.Element(f.Handler.RequestBodies[0]);

    [Fact]
    public async Task PostsToThePersonalReposEndpointByDefault()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));

        var req = Assert.Single(f.Handler.Requests);
        Assert.Equal("https://api.github.com/user/repos", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
    }

    // With an owner it goes to the org endpoint instead, URL-escaped.
    [Fact]
    public async Task PostsToTheOrgEndpointWhenAnOwnerIsGiven()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId, ("owner", "acme corp")));

        // AbsoluteUri, not ToString(): the latter decodes the %20 back.
        Assert.Equal("https://api.github.com/orgs/acme%20corp/repos",
            f.Handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    // The decrypted PAT rides as the bearer — never the ciphertext.
    [Fact]
    public async Task SendsTheDecryptedTokenAsABearer()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential(pat: "ghp_supersecret");

        await Run(f, Args(credId));

        var auth = f.Handler.Requests[0].Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("ghp_supersecret", auth.Parameter);
    }

    // GitHub requires the version + UA headers; without them the API 403s.
    [Fact]
    public async Task SendsTheRequiredGithubHeaders()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));

        var req = f.Handler.Requests[0];
        Assert.Equal("2022-11-28", Assert.Single(req.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Contains(req.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.Contains(req.Headers.UserAgent, u => u.Product?.Name == "FlowWeaver");
    }

    // HTTP/2 against api.github.com has been observed to hang ~20s on this
    // POST, so the request is pinned to 1.1.
    [Fact]
    public async Task PinsHttp11ToAvoidTheObservedHang()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));

        var req = f.Handler.Requests[0];
        Assert.Equal(System.Net.HttpVersion.Version11, req.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, req.VersionPolicy);
    }

    // Repos default to private — creating a public repo by accident is the
    // expensive mistake.
    [Fact]
    public async Task DefaultsToAPrivateRepositoryWithAutoInit()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));

        var body = SentBody(f);
        Assert.True(body.GetProperty("private").GetBoolean());
        Assert.True(body.GetProperty("auto_init").GetBoolean());
        Assert.Equal("infra", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task HonoursAnExplicitPublicRequest()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId, ("private", false)));

        Assert.False(SentBody(f).GetProperty("private").GetBoolean());
    }

    [Fact]
    public async Task DescriptionIsOnlySentWhenProvided()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));
        Assert.False(SentBody(f).TryGetProperty("description", out _));
    }

    // `main` is GitHub's own default, so sending it is redundant; anything
    // else must be forwarded.
    [Fact]
    public async Task DefaultBranchIsOnlySentWhenItDiffersFromMain()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));
        Assert.False(SentBody(f).TryGetProperty("default_branch", out _));

        using var f2 = new Fixture();
        var credId2 = f2.SeedCredential();
        await Run(f2, Args(credId2, ("default_branch", "trunk")));
        Assert.Equal("trunk", SentBody(f2).GetProperty("default_branch").GetString());
    }

    // ─── GitHub failures ────────────────────────────────────────────────

    // The API body carries the real reason (name taken, scope missing); it
    // must reach the agent verbatim.
    [Fact]
    public async Task GithubErrorIsSurfacedVerbatim()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();
        f.Handler = new FakeHttpMessageHandler(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Repository creation failed.","errors":[{"message":"name already exists"}]}""");

        var error = ErrorOf(await Run(f, Args(credId)));

        Assert.Contains("GitHub returned 422", error);
        Assert.Contains("name already exists", error);
    }

    [Fact]
    public async Task NetworkFailureIsReportedNotThrown()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();
        f.Handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection reset"));

        var error = ErrorOf(await Run(f, Args(credId)));

        Assert.Contains("GitHub API call failed", error);
        Assert.Contains("connection reset", error);
    }

    [Fact]
    public async Task NonJsonSuccessBodyIsReported()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.Created, "<html>gateway</html>", "text/html");

        Assert.Contains("was not JSON", ErrorOf(await Run(f, Args(credId))));
    }

    // ─── success + FlowWeaver registration ──────────────────────────────

    [Fact]
    public async Task SuccessReturnsTheRepositoryCoordinates()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        var result = await Run(f, Args(credId));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("acme/infra", result.GetProperty("full_name").GetString());
        Assert.Equal("https://github.com/acme/infra.git", result.GetProperty("clone_url").GetString());
        Assert.Equal("git@github.com:acme/infra.git", result.GetProperty("ssh_url").GetString());
        Assert.Equal("main", result.GetProperty("default_branch").GetString());
    }

    // Registering means the agent can pass repository_id straight to
    // git_write_file on its next turn.
    [Fact]
    public async Task RegistersInFlowWeaverByDefaultAndReturnsTheId()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        var result = await Run(f, Args(credId));

        Assert.True(result.GetProperty("registered_in_flowweaver").GetBoolean());
        Assert.Equal(f.Git.RepositoryId, result.GetProperty("repository_id").GetGuid());
        Assert.Equal("https://github.com/acme/infra.git", f.Git.CreatedWith!.Url);
        Assert.Equal(credId, f.Git.CreatedWith.AuthCredentialId);
    }

    [Fact]
    public async Task RegistrationCanBeSkipped()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        var result = await Run(f, Args(credId, ("register_in_flowweaver", false)));

        Assert.False(result.GetProperty("registered_in_flowweaver").GetBoolean());
        Assert.Null(f.Git.CreatedWith);
    }

    [Fact]
    public async Task RegistrationUsesTheOverrideNameWhenGiven()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId, ("register_name", "infra-prod")));

        Assert.Equal("infra-prod", f.Git.CreatedWith!.Name);
    }

    [Fact]
    public async Task RegistrationFallsBackToTheRepositoryName()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();

        await Run(f, Args(credId));

        Assert.Equal("infra", f.Git.CreatedWith!.Name);
    }

    // The critical one: the GitHub repo already exists, so a registration
    // failure must not read as a failed call — otherwise the agent retries and
    // creates a second repository.
    [Fact]
    public async Task RegistrationFailureStillReportsSuccessWithTheReason()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();
        f.Git.FailWith = new ConflictObjectResult(new { error = "name already used" });

        var result = await Run(f, Args(credId));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("registered_in_flowweaver").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("repository_id").ValueKind);
        Assert.Contains("name already used", result.GetProperty("register_error").GetString());
    }

    // The branch GitHub actually created wins over the requested one.
    [Fact]
    public async Task TheApiReportedBranchIsUsedForRegistration()
    {
        using var f = new Fixture();
        var credId = f.SeedCredential();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.Created, GithubRepo(defaultBranch: "trunk"));

        var result = await Run(f, Args(credId, ("default_branch", "develop")));

        Assert.Equal("trunk", result.GetProperty("default_branch").GetString());
        Assert.Equal("trunk", f.Git.CreatedWith!.DefaultBranch);
    }
}
