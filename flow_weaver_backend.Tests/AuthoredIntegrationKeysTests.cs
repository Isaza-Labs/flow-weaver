using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Tests;

// A python step is handed the auth headers of every integration its payload
// names under `*_integration_id`, and the script can print them. Only the
// workflow's author may pick those integrations: not the run input, not a
// template that resolves to run-time data.
public class AuthoredIntegrationKeysTests
{
    private const string Netbox = "11111111-1111-1111-1111-111111111111";
    private const string Vault = "22222222-2222-2222-2222-222222222222";

    private static JsonElement J(string json) => TestJson.Element(json);

    // Same merge the executor runs (run input first, config second).
    private static JsonElement Merge(string runInput, string config)
        => (JsonElement)typeof(WorkflowExecutor)
            .GetMethod("MergePayloads", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { J(runInput), J(config) })!;

    [Fact]
    public void ALiteralIdInTheNodeConfigIsAuthored()
    {
        var config = J($$"""{ "netbox_integration_id": "{{Netbox}}" }""");

        Assert.Empty(AuthoredIntegrationKeys.Unauthored(config, config));
    }

    [Fact]
    public void AnIdSuppliedByTheRunInputIsNot()
    {
        var payload = Merge($$"""{ "vault_integration_id": "{{Vault}}" }""", "{}");

        Assert.Equal(new[] { "vault_integration_id" },
            AuthoredIntegrationKeys.Unauthored(payload, J("{}")));
    }

    // The handler takes the FIRST occurrence of a key, and the merge writes the
    // run input's copy first — so a run input could override an authored id.
    [Fact]
    public void ARunInputCopyOfAnAuthoredKeyIsStillCaught()
    {
        var config = $$"""{ "netbox_integration_id": "{{Netbox}}" }""";
        var payload = Merge($$"""{ "netbox_integration_id": "{{Vault}}" }""", config);

        Assert.Equal(new[] { "netbox_integration_id" },
            AuthoredIntegrationKeys.Unauthored(payload, J(config)));
    }

    [Fact]
    public void ATemplatedIdIsNotAuthoredEvenIfItResolvesToTheSameText()
    {
        var config = J("""{ "netbox_integration_id": "{{ input.nb }}" }""");
        var resolved = J($$"""{ "netbox_integration_id": "{{Netbox}}" }""");

        Assert.Single(AuthoredIntegrationKeys.Unauthored(resolved, config));
    }

    // PythonHandler reads `input` instead of the payload when it is present.
    [Fact]
    public void KeysInsideInputFollowTheSameRule()
    {
        var payload = Merge($$"""{ "input": { "vault_integration_id": "{{Vault}}" } }""", "{}");

        Assert.Equal(new[] { "input.vault_integration_id" },
            AuthoredIntegrationKeys.Unauthored(payload, J("{}")));

        var authored = J($$"""{ "input": { "vault_integration_id": "{{Vault}}" } }""");
        Assert.Empty(AuthoredIntegrationKeys.Unauthored(authored, authored));
    }

    [Theory]
    [InlineData("""{ "netbox_integration_id": 42 }""")]
    [InlineData("""{ "netbox_integration_id": "  " }""")]
    [InlineData("""{ "integration": "netbox", "other": "x" }""")]
    public void EntriesTheHandlerIgnoresAreIgnored(string payload)
        => Assert.Empty(AuthoredIntegrationKeys.Unauthored(J(payload), J("{}")));

    // A subflow child trusts its own input: it was built from the parent's
    // subflow node and filtered with Strip.
    [Fact]
    public void ASubflowChildTrustsItsInheritedInput()
    {
        var inherited = J($$"""{ "netbox_integration_id": "{{Netbox}}" }""");
        var payload = Merge(inherited.GetRawText(), "{}");

        Assert.Empty(AuthoredIntegrationKeys.Unauthored(payload, J("{}"), inherited));
    }

    [Fact]
    public void StripKeepsTheAuthoredCopyAndDropsTheRest()
    {
        var config = $$"""{ "netbox_integration_id": "{{Netbox}}" }""";
        var payload = Merge(
            $$"""{ "netbox_integration_id": "{{Vault}}", "vault_integration_id": "{{Vault}}", "keep": "me" }""",
            config);

        var stripped = AuthoredIntegrationKeys.Strip(payload, J(config));

        Assert.Empty(AuthoredIntegrationKeys.Unauthored(stripped, J(config)));
        var netbox = stripped.EnumerateObject().Where(p => p.Name == "netbox_integration_id").ToList();
        Assert.Equal(Netbox, Assert.Single(netbox).Value.GetString());
        Assert.False(stripped.TryGetProperty("vault_integration_id", out _));
        Assert.Equal("me", stripped.GetProperty("keep").GetString());
    }

    // PythonHandler reads `input` INSTEAD of the payload when it is present, so
    // a top-level key is dead data then — failing the step over it would send the
    // user chasing a key the handler never reads.
    [Fact]
    public void ATopLevelKeyIsIgnoredWhenThePayloadHasAnInputObject()
    {
        var config = """{ "input": { "x": 1 } }""";
        var payload = Merge($$"""{ "vault_integration_id": "{{Vault}}" }""", config);

        Assert.Empty(AuthoredIntegrationKeys.Unauthored(payload, J(config)));
    }

    // `_integration_id` alone leaves an empty handle, which the handler skips.
    [Fact]
    public void AnEmptyHandleIsIgnored()
    {
        var payload = Merge($$"""{ "_integration_id": "{{Vault}}" }""", "{}");

        Assert.Empty(AuthoredIntegrationKeys.Unauthored(payload, J("{}")));
    }

    [Fact]
    public void StripWithNothingToRemoveReturnsTheSamePayload()
    {
        var payload = J("""{ "a": 1 }""");

        Assert.Equal(payload.GetRawText(), AuthoredIntegrationKeys.Strip(payload, J("{}")).GetRawText());
    }
}
