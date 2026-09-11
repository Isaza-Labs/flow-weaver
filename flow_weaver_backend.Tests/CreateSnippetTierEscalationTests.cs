using System.Text.Json;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Ai.Tools;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// PermissionClassifier's matrix is keyed by tool NAME, so create_snippet was
// single_confirm for every call — including the one that sets
// network_enabled=true, which lifts the python sandbox's network isolation
// (--share-net) and unlocks the socket/paramiko/netmiko import set.
//
// The service-level admin check is not sufficient on its own: everything the
// agent reads is untrusted input (SSH output, a git file, an MCP tool result, a
// chat message), so an admin's session is exactly where a prompt injection
// would aim. A role check doesn't help when the role is the vehicle. The tier
// escalation makes the agent refuse regardless of role and hand the job to a
// human in the Snippets UI.
public class CreateSnippetTierEscalationTests
{
    private static CreateSnippetHandler Handler() =>
        new(snippets: null!, NullLogger<CreateSnippetHandler>.Instance);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Network_enabled_escalates_to_human_only()
    {
        var tier = Handler().EscalatedTier(Args("""
            {"name":"x","type":"python_snippet","target_mode":"once","network_enabled":true}
            """));

        Assert.Equal(PermissionClassifier.TierHumanOnly, tier);
    }

    [Theory]
    [InlineData("""{"name":"x","type":"python_snippet","target_mode":"once"}""")]
    [InlineData("""{"name":"x","type":"python_snippet","target_mode":"once","network_enabled":false}""")]
    [InlineData("""{"name":"x","type":"ping","target_mode":"per_device"}""")]
    public void An_ordinary_snippet_keeps_its_static_tier(string json)
    {
        Assert.Null(Handler().EscalatedTier(Args(json)));
    }

    [Theory]
    // A non-boolean value doesn't set NetworkEnabled either (CreateSnippet.Bool
    // only accepts a JSON bool), so "no escalation" and "not network-enabled"
    // stay consistent — the snippet is created WITHOUT the flag rather than
    // being refused for a flag it never got.
    [InlineData("""{"network_enabled":"true"}""")]
    [InlineData("""{"network_enabled":1}""")]
    [InlineData("""{"network_enabled":null}""")]
    [InlineData("{}")]
    public void Non_boolean_values_do_not_escalate(string json)
    {
        Assert.Null(Handler().EscalatedTier(Args(json)));
    }

    [Fact]
    public void The_handler_advertises_the_refusal_in_its_description()
    {
        // The agent decides what to call from the description. Saying it will
        // always be refused is what stops it burning a turn on a doomed call
        // and then retrying.
        var description = Handler().Description;
        Assert.Contains("human_only", description);
        Assert.Contains("network_enabled", description);
    }
}
