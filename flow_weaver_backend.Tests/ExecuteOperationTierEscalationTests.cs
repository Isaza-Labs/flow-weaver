using System.Text.Json;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// create_snippet escalates network_enabled=true to human_only (see
// CreateSnippetTierEscalationTests). The same flag also reaches SnippetService
// through execute_operation → fw_snippets:create_snippet / update_snippet, with
// the session bearer — so without this an admin chat could still grant it, and
// on UPDATE, which create_snippet never covered. One flag, one gate.
public class ExecuteOperationTierEscalationTests
{
    private static ExecuteOperationHandler Handler() =>
        new(executor: null!, NullLogger<ExecuteOperationHandler>.Instance);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("""{"operation_id":"fw_snippets:create_snippet","body":{"name":"x","type":"python_snippet","network_enabled":true}}""")]
    [InlineData("""{"operation_id":"fw_snippets:update_snippet","path_params":{"id":"00000000-0000-0000-0000-000000000001"},"body":{"network_enabled":true}}""")]
    [InlineData("""{"operation_id":"FW_SNIPPETS:UPDATE_SNIPPET","body":{"network_enabled":true}}""")]
    // A body sent as a JSON-encoded string is still looked inside.
    [InlineData("""{"operation_id":"fw_snippets:update_snippet","body":"{\"network_enabled\":true}"}""")]
    public void Setting_network_enabled_through_the_self_api_escalates_to_human_only(string json)
    {
        Assert.Equal(PermissionClassifier.TierHumanOnly, Handler().EscalatedTier(Args(json)));
    }

    [Theory]
    [InlineData("""{"operation_id":"fw_snippets:update_snippet","body":{"code":"print(1)"}}""")]
    [InlineData("""{"operation_id":"fw_snippets:update_snippet","body":{"network_enabled":false}}""")]
    [InlineData("""{"operation_id":"fw_snippets:create_snippet","body":{"network_enabled":"true"}}""")]
    [InlineData("""{"operation_id":"fw_snippets:create_snippet"}""")]
    [InlineData("""{"operation_id":"fw_snippets:update_snippet","body":"not json"}""")]
    // Another operation carrying the same key is not a snippet write.
    [InlineData("""{"operation_id":"fw_workflows:update_workflow","body":{"network_enabled":true}}""")]
    [InlineData("""{"body":{"network_enabled":true}}""")]
    public void Everything_else_keeps_the_static_tier(string json)
    {
        Assert.Null(Handler().EscalatedTier(Args(json)));
    }
}
