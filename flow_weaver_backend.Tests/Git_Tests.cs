using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Tests;

// GitService registration CRUD + validation are DB-only (no LibGit2Sharp). The
// actual clone/read/commit ops need a working tree, so here we cover the
// registration surface + the repo-not-found guard of the file ops.
public class GitServiceTests
{
    private readonly FakeUser _caller = new();

    private GitService NewSvc(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:Root"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fw-git-test-" + Guid.NewGuid().ToString("N")),
            })
            .Build();
        return new GitService(new GitRepositoryRepository(db), new RepositoryBase<CredentialModel>(db), _caller,
            new FakeCrypto(), config, new FakeHostEnvironment(), new FakeAudit(), NullLogger<GitService>.Instance);
    }

    private static CreateGitRepository Sample(string name = "infra")
        => new() { Name = name, Url = "https://github.com/acme/infra.git", DefaultBranch = "main" };

    private static int? Status(Microsoft.AspNetCore.Mvc.IActionResult? r) => HttpResult.Status(r);

    [Fact]
    public async Task Create_persists_registration()
    {
        using var db = TestDb.NewContext();
        await NewSvc(db).CreateAsync(Sample(), CancellationToken.None);
        Assert.Single(db.Set<GitRepository>());
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db).CreateAsync(new CreateGitRepository { Name = "", Url = "https://x/y.git" }, CancellationToken.None);
        Assert.Equal(400, Status(r.Result));
    }

    [Fact]
    public async Task Create_rejects_non_https_url()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db).CreateAsync(new CreateGitRepository { Name = "x", Url = "http://github.com/a/b.git" }, CancellationToken.None);
        Assert.Equal(400, Status(r.Result));
    }

    [Fact]
    public async Task Create_rejects_duplicate_name()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db);
        await svc.CreateAsync(Sample(), CancellationToken.None);
        var dup = await svc.CreateAsync(Sample(), CancellationToken.None);
        Assert.Equal(409, Status(dup.Result));
    }

    [Fact]
    public async Task Get_missing_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.Equal(404, Status((await NewSvc(db).GetAsync(Guid.NewGuid(), CancellationToken.None)).Result));
    }

    [Fact]
    public async Task Update_missing_returns_404()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db).UpdateAsync(Guid.NewGuid(), new UpdateGitRepository { Description = "x" }, CancellationToken.None);
        Assert.Equal(404, Status(r.Result));
    }

    [Fact]
    public async Task Delete_missing_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.Equal(404, Status((await NewSvc(db).DeleteAsync(Guid.NewGuid(), CancellationToken.None)).Result));
    }

    [Fact]
    public async Task List_and_get_created()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db);
        await svc.CreateAsync(Sample(), CancellationToken.None);

        var list = await svc.ListAsync(50, 0, CancellationToken.None);
        // Success returns the payload directly (ActionResult<T>.Value), not an OkObjectResult.
        var page = list.Value ?? (list.Result as Microsoft.AspNetCore.Mvc.ObjectResult)?.Value as ListResponse<GitRepositoryResponse>;
        Assert.Equal(1, page!.Total);
    }

    [Fact]
    public async Task File_ops_on_missing_repo_return_404()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db);
        Assert.Equal(404, Status((await svc.ReadFileAsync(Guid.NewGuid(), "README.md", null, CancellationToken.None)).Result));
        Assert.Equal(404, Status((await svc.ListFilesAsync(Guid.NewGuid(), null, null, CancellationToken.None)).Result));
    }
}
