using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Ai.Tools.Handlers.Git;
using flow_weaver_backend.Services.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Tests;

// Git AI tool handlers delegate to IGitService (now constructible). For a random
// repository_id the service returns 404, which each handler wraps into a JSON
// object — so ExecuteAsync always yields a JSON payload without throwing.
public class GitToolHandlerTests
{
    private readonly FakeUser _caller = new();

    private GitService NewGit(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:Root"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fw-git-h-" + Guid.NewGuid().ToString("N")),
            }).Build();
        return new GitService(new GitRepositoryRepository(db), new RepositoryBase<CredentialModel>(db), _caller,
            new FakeCrypto(), config, new FakeHostEnvironment(), new FakeAudit(), NullLogger<GitService>.Instance);
    }

    private ILogger<T> Log<T>() => NullLogger<T>.Instance;
    private static JsonElement A(object o) => JsonSerializer.SerializeToElement(o);
    private static void Ok(JsonElement e) => Assert.True(e.ValueKind is JsonValueKind.Object or JsonValueKind.Array, $"got {e.ValueKind}");

    [Fact]
    public async Task git_list_repositories()
    {
        using var db = TestDb.NewContext();
        var h = new GitListRepositoriesHandler(NewGit(db));
        Assert.Equal("git_list_repositories", h.Name);
        Ok(await h.ExecuteAsync(A(new { limit = 20 }), CancellationToken.None));
    }

    [Fact]
    public async Task git_list_files_missing_repo()
    {
        using var db = TestDb.NewContext();
        var h = new GitListFilesHandler(NewGit(db));
        Ok(await h.ExecuteAsync(A(new { repository_id = Guid.NewGuid(), path = "" }), CancellationToken.None));
    }

    [Fact]
    public async Task git_read_file_missing_repo()
    {
        using var db = TestDb.NewContext();
        var h = new GitReadFileHandler(NewGit(db));
        Ok(await h.ExecuteAsync(A(new { repository_id = Guid.NewGuid(), path = "README.md" }), CancellationToken.None));
    }

    [Fact]
    public async Task git_diff_missing_repo()
    {
        using var db = TestDb.NewContext();
        var h = new GitDiffHandler(NewGit(db));
        Ok(await h.ExecuteAsync(A(new { repository_id = Guid.NewGuid() }), CancellationToken.None));
    }

    [Fact]
    public async Task git_pull_missing_repo()
    {
        using var db = TestDb.NewContext();
        var h = new GitPullHandler(NewGit(db));
        Ok(await h.ExecuteAsync(A(new { repository_id = Guid.NewGuid() }), CancellationToken.None));
    }
}
