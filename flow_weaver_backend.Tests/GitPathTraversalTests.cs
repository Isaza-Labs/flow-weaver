using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Tests;

// Traceability: B-06 (TC-FW-B06) — path-traversal hardening. GitService's
// NormalizePath rejects `..`/`.` segments so a template-supplied repo path
// can't escape the configured repo dir, and IsSshUrl/allow-list keeps the clone
// transport to https/ssh only.
//
// NOTE: this covers the Git repo-path guard specifically. B-06's prompt-
// construction / skill-file read-path variant is a separate call site and is
// still an open gap (see the traceability note).
public class GitPathTraversalTests
{
    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("./secret")]
    [InlineData("foo/./bar")]
    [InlineData("..")]
    public void Rejects_traversal_segments(string path)
        => Assert.Equal(string.Empty, GitService.NormalizePath(path));

    [Theory]
    [InlineData("dir/file.md", "dir/file.md")]
    [InlineData("\\dir\\file", "dir/file")]     // backslashes normalized to /
    [InlineData("/leading/slash", "leading/slash")]  // leading slash stripped
    [InlineData("trailing/", "trailing")]
    public void Normalizes_safe_relative_paths(string input, string expected)
        => Assert.Equal(expected, GitService.NormalizePath(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_null_becomes_empty(string? path)
        => Assert.Equal(string.Empty, GitService.NormalizePath(path));

    [Theory]
    [InlineData("git@github.com:org/repo.git")]        // scp-style
    [InlineData("ssh://git@host.example/org/repo.git")] // explicit ssh URI
    public void Accepts_ssh_url_forms(string url)
        => Assert.True(GitService.IsSshUrl(url));

    [Theory]
    [InlineData("https://github.com/org/repo.git")]  // https handled elsewhere, not "ssh"
    [InlineData("file:///etc/passwd")]
    [InlineData("git://host/repo.git")]
    [InlineData("not-a-url")]
    public void Rejects_non_ssh_urls(string url)
        => Assert.False(GitService.IsSshUrl(url));
}
