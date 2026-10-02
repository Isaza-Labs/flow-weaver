using LibGit2Sharp;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Tests;

// A branch created through the git tools could be checked out, written to and
// committed, and then never reached the remote:
//
//   The branch 'test/git-flow' ("refs/heads/test/git-flow") that you are trying
//   to push does not track an upstream branch.
//
// while writing to `main` pushed fine. That difference is the bug: `main` was
// cloned and already tracks a remote branch; a branch created locally does not,
// and `Network.Push(Branch, …)` refuses one that does not.
//
// This product had the identical call in two places.
// The SSH path is not covered here — it shells out to the git CLI, which sets
// the upstream itself.
//
// These run against a real bare repository on disk, because a push is the one
// thing a mock cannot tell you anything about.
public class GitBranchPublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fw-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _bare;
    private readonly string _work;

    public GitBranchPublishTests()
    {
        _bare = Path.Combine(_root, "origin.git");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_bare);
        Repository.Init(_bare, isBare: true);

        Repository.Init(_work);
        using var repo = new Repository(_work);
        // libgit2 needs an absolute file URL for a refspec push; a relative path
        // fails with "unsupported URL protocol".
        repo.Network.Remotes.Add("origin", "file:///" + _bare.Replace('\\', '/'));
        Commit(repo, "README.md", "base");
        repo.Network.Push(repo.Network.Remotes["origin"], "refs/heads/master:refs/heads/master");
        repo.Branches.Update(repo.Head,
            b => b.Remote = "origin",
            b => b.UpstreamBranch = repo.Head.CanonicalName);
    }

    private static void Commit(Repository repo, string file, string content)
    {
        File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, file), content + "\n");
        Commands.Stage(repo, file);
        var who = new Signature("QA", "qa@example.invalid", DateTimeOffset.Now);
        repo.Commit($"write {file}", who, who);
    }

    private Branch NewBranchWithCommit(Repository repo, string name, string file)
    {
        var branch = repo.CreateBranch(name);
        Commands.Checkout(repo, branch);
        Commit(repo, file, "prueba");
        return repo.Branches[name];
    }

    private string[] RemoteBranches()
    {
        using var bare = new Repository(_bare);
        return bare.Branches.Select(b => b.FriendlyName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void A_new_branch_does_not_track_anything_and_the_plain_push_refuses_it()
    {
        // The state the tools were in. Kept as a test so the fix is not mistaken
        // for a workaround: this is the behaviour being routed around, and it is
        // LibGit2Sharp's, not ours.
        using var repo = new Repository(_work);
        var branch = NewBranchWithCommit(repo, "test/git-flow", "prueba.txt");

        Assert.False(branch.IsTracking);
        var ex = Assert.Throws<LibGit2SharpException>(
            () => repo.Network.Push(branch, new PushOptions()));
        Assert.Contains("does not track an upstream branch", ex.Message);
        Assert.DoesNotContain("test/git-flow", RemoteBranches());
    }

    [Fact]
    public void Publishing_a_new_branch_puts_it_on_the_remote_and_records_the_upstream()
    {
        using var repo = new Repository(_work);
        var branch = NewBranchWithCommit(repo, "test/git-flow", "prueba.txt");

        var published = GitService.PushBranch(repo, branch, new PushOptions());

        Assert.True(published);
        Assert.Contains("test/git-flow", RemoteBranches());

        var after = repo.Branches["test/git-flow"];
        Assert.True(after.IsTracking);
        Assert.Equal("refs/heads/test/git-flow", after.UpstreamBranchCanonicalName);
    }

    [Fact]
    public void A_second_push_takes_the_ordinary_path_and_still_lands()
    {
        using var repo = new Repository(_work);
        var branch = NewBranchWithCommit(repo, "test/git-flow", "prueba.txt");
        GitService.PushBranch(repo, branch, new PushOptions());

        Commit(repo, "prueba.txt", "otra vez");
        var published = GitService.PushBranch(repo, repo.Branches["test/git-flow"], new PushOptions());

        // Already tracking, so nothing to publish — but the commit must arrive.
        Assert.False(published);
        using var bare = new Repository(_bare);
        Assert.Equal(repo.Branches["test/git-flow"].Tip.Sha, bare.Branches["test/git-flow"].Tip.Sha);
    }

    [Fact]
    public void The_remote_only_gains_the_branch_that_was_pushed()
    {
        using var repo = new Repository(_work);
        NewBranchWithCommit(repo, "test/keep-local", "local.txt");
        var published = NewBranchWithCommit(repo, "test/publish-me", "published.txt");

        GitService.PushBranch(repo, published, new PushOptions());

        Assert.Contains("test/publish-me", RemoteBranches());
        Assert.DoesNotContain("test/keep-local", RemoteBranches());
    }

    public void Dispose()
    {
        try
        {
            // libgit2 leaves read-only objects behind; Directory.Delete refuses those.
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* a temp directory is not worth failing a test over */ }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }
}
