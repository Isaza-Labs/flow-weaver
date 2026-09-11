using System.Text;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Tests;

public class GitWebhookPayloadTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Github_push_extracts_branch_and_head_commit()
    {
        var body = B("""
        {
          "ref": "refs/heads/main",
          "head_commit": { "id": "abcdef0123" },
          "after": "ignoredinpresenceofheadcommit"
        }
        """);
        var p = GitWebhookPayload.Parse("github", "push", body);
        Assert.Equal("push", p.Event);
        Assert.Equal("main", p.Branch);
        Assert.Equal("abcdef0123", p.CommitSha);
    }

    [Fact]
    public void Github_tag_push_branch_is_null()
    {
        // refs/tags/* is not a branch; we explicitly DON'T match it as one.
        var body = B("""
        { "ref": "refs/tags/v1.0", "head_commit": { "id": "deadbeef" } }
        """);
        var p = GitWebhookPayload.Parse("github", "push", body);
        Assert.Null(p.Branch);
        Assert.Equal("deadbeef", p.CommitSha);
    }

    [Fact]
    public void Github_branch_deletion_zero_sha_is_normalized_to_null()
    {
        var body = B("""
        { "ref": "refs/heads/feature", "head_commit": null,
          "after": "0000000000000000000000000000000000000000" }
        """);
        var p = GitWebhookPayload.Parse("github", "push", body);
        Assert.Equal("feature", p.Branch);
        Assert.Null(p.CommitSha);
    }

    [Fact]
    public void Gitlab_push_uses_object_kind_and_checkout_sha()
    {
        var body = B("""
        {
          "object_kind": "push",
          "ref": "refs/heads/develop",
          "checkout_sha": "feedface"
        }
        """);
        var p = GitWebhookPayload.Parse("gitlab", null, body);
        Assert.Equal("push", p.Event);
        Assert.Equal("develop", p.Branch);
        Assert.Equal("feedface", p.CommitSha);
    }

    [Fact]
    public void Generic_after_field_is_used_when_no_head_commit()
    {
        var body = B("""
        { "ref": "refs/heads/main", "after": "fallback-sha" }
        """);
        var p = GitWebhookPayload.Parse("generic", "push", body);
        Assert.Equal("fallback-sha", p.CommitSha);
    }

    [Fact]
    public void Malformed_json_returns_event_only()
    {
        var p = GitWebhookPayload.Parse("github", "push", B("this is not json"));
        Assert.Equal("push", p.Event);
        Assert.Null(p.Branch);
        Assert.Null(p.CommitSha);
    }

    [Fact]
    public void Empty_body_returns_event_header_only()
    {
        var p = GitWebhookPayload.Parse("github", "ping", Array.Empty<byte>());
        Assert.Equal("ping", p.Event);
        Assert.Null(p.Branch);
    }
}
