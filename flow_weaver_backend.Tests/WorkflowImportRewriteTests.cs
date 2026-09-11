using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Controllers;

namespace flow_weaver_backend.Tests;

// The node-rewriting half of the import commit: remapping placeholder ids to
// the real rows just created, and redistributing a foreign adapter's flat
// bag-of-params into the `params` / `query` / `body` buckets the runtime
// actually reads. Get the redistribution wrong and the node commits fine but
// sends a path parameter as a query string at run time.
public class WorkflowImportRewriteTests
{
    private static readonly Type Controller = typeof(WorkflowImportController);

    private static MethodInfo Method(string name)
        => Controller.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    // The remap the rewriters take for an integration_action stub: stub id →
    // the ids/names resolved during commit.
    private static ActionRemapMap ActionRemap(
        string stubId, Guid integrationId, Guid actionId, string integrationName, string actionName)
        => new() { [stubId] = (integrationId, actionId, integrationName, actionName) };

    private sealed class ActionRemapMap
        : Dictionary<string, (Guid IntegrationId, Guid ActionId, string IntegrationName, string ActionName)>
    {
    }

    // ─── RewriteReferences ──────────────────────────────────────────────

    private static JsonElement RewriteReferences(
        string workflow,
        Dictionary<string, Guid>? snippetRemap = null,
        Dictionary<string, Guid>? integrationRemap = null,
        ActionRemapMap? actionRemap = null)
        => (JsonElement)Method("RewriteReferences").Invoke(null, new object?[]
        {
            TestJson.Element(workflow),
            snippetRemap ?? new Dictionary<string, Guid>(),
            integrationRemap ?? new Dictionary<string, Guid>(),
            actionRemap
                ?? new Dictionary<string, (Guid, Guid, string, string)>()
                    as object,
        })!;

    // The core of the commit: placeholder snippet ids become the real ones.
    [Fact]
    public void Rewrite_ReplacesSnippetIdsWithTheNewlyCreatedOnes()
    {
        var newId = Guid.NewGuid();

        var result = RewriteReferences(
            """{"name":"wf","nodes":[{"id":"n1","snippet_id":"imported-ssh"}]}""",
            snippetRemap: new Dictionary<string, Guid> { ["imported-ssh"] = newId });

        Assert.Equal(newId.ToString(), result.GetProperty("nodes")[0].GetProperty("snippet_id").GetString());
    }

    // An id with no remap entry survives — it was already a real GUID.
    [Fact]
    public void Rewrite_UnmappedSnippetIdsAreLeftAlone()
    {
        var existing = Guid.NewGuid().ToString();

        var result = RewriteReferences(
            $$"""{"nodes":[{"id":"n1","snippet_id":"{{existing}}"}]}""");

        Assert.Equal(existing, result.GetProperty("nodes")[0].GetProperty("snippet_id").GetString());
    }

    [Fact]
    public void Rewrite_RemapsIntegrationIdsInsideConfigOverrides()
    {
        var newId = Guid.NewGuid();

        var result = RewriteReferences(
            """{"nodes":[{"id":"n1","snippet_id":"s","config_overrides":{"integration_id":"imported-netbox"}}]}""",
            integrationRemap: new Dictionary<string, Guid> { ["imported-netbox"] = newId });

        Assert.Equal(newId.ToString(),
            result.GetProperty("nodes")[0].GetProperty("config_overrides")
                  .GetProperty("integration_id").GetString());
    }

    [Fact]
    public void Rewrite_PreservesEveryOtherTopLevelKey()
    {
        var result = RewriteReferences(
            """{"name":"wf","description":"d","edges":[{"source":"a","target":"b"}],"nodes":[]}""");

        Assert.Equal("wf", result.GetProperty("name").GetString());
        Assert.Equal("d", result.GetProperty("description").GetString());
        Assert.Single(result.GetProperty("edges").EnumerateArray());
    }

    [Fact]
    public void Rewrite_PreservesUnrelatedNodeFieldsAndConfig()
    {
        var result = RewriteReferences(
            """{"nodes":[{"id":"n1","snippet_id":"s","x":10,"config_overrides":{"cmd":"show ver","n":1}}]}""");

        var node = result.GetProperty("nodes")[0];
        Assert.Equal(10, node.GetProperty("x").GetInt32());
        Assert.Equal("show ver", node.GetProperty("config_overrides").GetProperty("cmd").GetString());
        Assert.Equal(1, node.GetProperty("config_overrides").GetProperty("n").GetInt32());
    }

    // ─── integration_action stubs ───────────────────────────────────────

    // A stub becomes the virtual `integration_action` sentinel: the runtime
    // dispatches via config_overrides and never looks up a Snippet row.
    [Fact]
    public void Rewrite_StubBecomesTheVirtualSentinelWithResolvedIds()
    {
        var integrationId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var remap = ActionRemap("stub-send-email", integrationId, actionId, "fw_email", "send_email");

        var result = RewriteReferences(
            """{"nodes":[{"id":"n1","snippet_id":"stub-send-email"}]}""",
            actionRemap: remap);

        var node = result.GetProperty("nodes")[0];
        Assert.Equal("integration_action", node.GetProperty("snippet_id").GetString());
        var co = node.GetProperty("config_overrides");
        Assert.Equal(integrationId.ToString(), co.GetProperty("integration_id").GetString());
        Assert.Equal(actionId.ToString(), co.GetProperty("action_id").GetString());
        Assert.Equal("fw_email", co.GetProperty("integration_name").GetString());
        Assert.Equal("send_email", co.GetProperty("action_name").GetString());
    }

    // The validator demands the ids, so a stub with NO config_overrides must
    // still get one synthesised.
    [Fact]
    public void Rewrite_StubWithoutConfigOverridesGetsOneSynthesised()
    {
        var remap = ActionRemap("stub", Guid.NewGuid(), Guid.NewGuid(), "fw_email", "send_email");

        var result = RewriteReferences(
            """{"nodes":[{"id":"n1","snippet_id":"stub"}]}""",
            actionRemap: remap);

        Assert.True(result.GetProperty("nodes")[0].TryGetProperty("config_overrides", out var co));
        Assert.True(co.TryGetProperty("action_id", out _));
    }

    // Stale strings the YAML carried must not shadow the resolved values.
    [Fact]
    public void Rewrite_StaleActionFieldsAreReplacedNotKept()
    {
        var actionId = Guid.NewGuid();
        var remap = ActionRemap("stub", Guid.NewGuid(), actionId, "fw_email", "send_email");

        var result = RewriteReferences(
            """
            {"nodes":[{"id":"n1","snippet_id":"stub","config_overrides":{
              "action_id":"STALE","action_name":"old_name","integration_name":"old_integration","keep":"me"}}]}
            """,
            actionRemap: remap);

        var co = result.GetProperty("nodes")[0].GetProperty("config_overrides");
        Assert.Equal(actionId.ToString(), co.GetProperty("action_id").GetString());
        Assert.Equal("send_email", co.GetProperty("action_name").GetString());
        Assert.Equal("fw_email", co.GetProperty("integration_name").GetString());
        Assert.Equal("me", co.GetProperty("keep").GetString());   // unrelated keys survive
    }

    // The stub's resolved integration wins over a plain integration remap.
    [Fact]
    public void Rewrite_StubIntegrationIdWinsOverTheGenericRemap()
    {
        var stubIntegrationId = Guid.NewGuid();
        var remapTarget = Guid.NewGuid();
        var remap = ActionRemap("stub", stubIntegrationId, Guid.NewGuid(), "fw_email", "send_email");

        var result = RewriteReferences(
            """{"nodes":[{"id":"n1","snippet_id":"stub","config_overrides":{"integration_id":"other"}}]}""",
            integrationRemap: new Dictionary<string, Guid> { ["other"] = remapTarget },
            actionRemap: remap);

        Assert.Equal(stubIntegrationId.ToString(),
            result.GetProperty("nodes")[0].GetProperty("config_overrides")
                  .GetProperty("integration_id").GetString());
    }

    // ─── ExtractGeneratedActionHints ────────────────────────────────────

    private static (string? IntegrationName, string? ActionName, string? Method) Hints(string generated)
    {
        var result = Method("ExtractGeneratedActionHints").Invoke(null, new object?[] { TestJson.Element(generated) })!;
        var type = result.GetType();
        return (
            (string?)type.GetProperty("IntegrationName")!.GetValue(result),
            (string?)type.GetProperty("ActionName")!.GetValue(result),
            (string?)type.GetProperty("Method")!.GetValue(result));
    }

    [Fact]
    public void Hints_ReadsTopLevelFields()
    {
        var hints = Hints("""{"integration_name":"netbox","action_name":"list_devices","method":"GET"}""");

        Assert.Equal("netbox", hints.IntegrationName);
        Assert.Equal("list_devices", hints.ActionName);
        Assert.Equal("GET", hints.Method);
    }

    // The agent typically packs the metadata into `code`, which makes no
    // semantic sense for an integration_action — harvest it anyway.
    [Fact]
    public void Hints_ReadsFieldsInlinedUnderAnObjectCode()
    {
        var hints = Hints("""{"code":{"integration_name":"netbox","action_name":"list_devices"}}""");

        Assert.Equal("netbox", hints.IntegrationName);
        Assert.Equal("list_devices", hints.ActionName);
    }

    // ...and sometimes `code` is a JSON *string*.
    [Fact]
    public void Hints_ParsesAJsonStringCode()
    {
        var hints = Hints("""{"code":"{\"integration_name\":\"netbox\",\"method\":\"POST\"}"}""");

        Assert.Equal("netbox", hints.IntegrationName);
        Assert.Equal("POST", hints.Method);
    }

    // Free-form code is not JSON; harvesting must not throw.
    [Fact]
    public void Hints_FreeFormCodeYieldsNoHints()
    {
        var hints = Hints("""{"code":"def run(inp):\n    return {}"}""");

        Assert.Null(hints.IntegrationName);
        Assert.Null(hints.ActionName);
    }

    // Top-level wins: it's the more explicit place.
    [Fact]
    public void Hints_TopLevelTakesPrecedenceOverCode()
    {
        var hints = Hints("""{"integration_name":"top","code":{"integration_name":"nested"}}""");

        Assert.Equal("top", hints.IntegrationName);
    }

    // The agent emits REPLACE_* placeholders when it doesn't know the value —
    // those are "no hint", not a name to create an integration under.
    [Theory]
    [InlineData("REPLACE_ME")]
    [InlineData("replace_with_real_name")]
    [InlineData("REPLACE")]
    public void Hints_ReplacePlaceholdersAreTreatedAsAbsent(string placeholder)
    {
        var hints = Hints($$"""{"integration_name":"{{placeholder}}","action_name":"{{placeholder}}"}""");

        Assert.Null(hints.IntegrationName);
        Assert.Null(hints.ActionName);
    }

    // The method hint is NOT placeholder-stripped — only the two names are.
    [Fact]
    public void Hints_MethodIsNotPlaceholderStripped()
    {
        Assert.Equal("REPLACE_ME", Hints("""{"method":"REPLACE_ME"}""").Method);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"integration_name":123}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void Hints_MalformedInputYieldsNoHints(string generated)
    {
        var hints = Hints(generated);

        Assert.Null(hints.IntegrationName);
        Assert.Null(hints.ActionName);
        Assert.Null(hints.Method);
    }

    // ─── TryReadActionPair ──────────────────────────────────────────────

    private static (bool Ok, Guid IntegrationId, string ActionName, string NodeId) TryReadActionPair(string node)
    {
        var args = new object?[] { TestJson.Element(node), null, null, null };
        var ok = (bool)Method("TryReadActionPair").Invoke(null, args)!;
        return (ok, (Guid)args[1]!, (string)args[2]!, (string)args[3]!);
    }

    // The node that still needs resolving: a real integration id, a name, and
    // no action_id yet.
    [Fact]
    public void ActionPair_ReadsANodeThatNeedsResolving()
    {
        var integrationId = Guid.NewGuid();

        var (ok, id, actionName, nodeId) = TryReadActionPair(
            "{\"id\":\"n1\",\"snippet_id\":\"integration_action\","
            + "\"config_overrides\":{\"integration_id\":\"" + integrationId
            + "\",\"action_name\":\"list_devices\"}}");

        Assert.True(ok);
        Assert.Equal(integrationId, id);
        Assert.Equal("list_devices", actionName);
        Assert.Equal("n1", nodeId);
    }

    // Already resolved — nothing to do, so it must be skipped.
    [Fact]
    public void ActionPair_SkipsNodesThatAlreadyHaveAnActionId()
    {
        var (ok, _, _, _) = TryReadActionPair(
            "{\"id\":\"n1\",\"snippet_id\":\"integration_action\","
            + "\"config_overrides\":{\"integration_id\":\"" + Guid.NewGuid()
            + "\",\"action_name\":\"x\",\"action_id\":\"" + Guid.NewGuid() + "\"}}");

        Assert.False(ok);
    }

    // A non-GUID action_id is NOT a resolution, so the node still needs work.
    [Fact]
    public void ActionPair_APlaceholderActionIdStillNeedsResolving()
    {
        var (ok, _, _, _) = TryReadActionPair(
            "{\"id\":\"n1\",\"snippet_id\":\"integration_action\","
            + "\"config_overrides\":{\"integration_id\":\"" + Guid.NewGuid()
            + "\",\"action_name\":\"x\",\"action_id\":\"TODO\"}}");

        Assert.True(ok);
    }

    [Theory]
    [InlineData("""{"id":"n1","snippet_id":"other","config_overrides":{}}""")]           // not an integration_action
    [InlineData("""{"id":"n1","snippet_id":"integration_action"}""")]                    // no config
    [InlineData("""{"snippet_id":"integration_action","config_overrides":[]}""")]        // config not an object
    [InlineData("""[]""")]                                                               // not an object
    public void ActionPair_RejectsNodesItCannotResolve(string node)
    {
        Assert.False(TryReadActionPair(node).Ok);
    }

    // A non-GUID integration_id cannot be looked up.
    [Fact]
    public void ActionPair_RejectsANonGuidIntegrationId()
    {
        var (ok, _, _, _) = TryReadActionPair("""
            {"id":"n1","snippet_id":"integration_action",
             "config_overrides":{"integration_id":"imported-netbox","action_name":"x"}}
            """);

        Assert.False(ok);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("123")]
    public void ActionPair_RejectsAMissingOrBlankActionName(string actionName)
    {
        var (ok, _, _, _) = TryReadActionPair(
            "{\"id\":\"n1\",\"snippet_id\":\"integration_action\","
            + "\"config_overrides\":{\"integration_id\":\"" + Guid.NewGuid()
            + "\",\"action_name\":" + actionName + "}}");

        Assert.False(ok);
    }

    // ─── WriteResolvedNode (the params/query/body redistribution) ───────

    private static JsonElement WriteResolvedNode(
        string node, Guid actionId, string method, string path, string? integrationName = "netbox")
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            Method("WriteResolvedNode").Invoke(null, new object?[]
            {
                writer, TestJson.Element(node), actionId, method, path, integrationName,
            });
        }
        return TestJson.Element(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void Resolved_WritesTheResolvedIdentifiersFirst()
    {
        var actionId = Guid.NewGuid();

        var node = WriteResolvedNode(
            """{"id":"n1","snippet_id":"integration_action","config_overrides":{"integration_id":"abc"}}""",
            actionId, "GET", "/dcim/devices");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("abc", co.GetProperty("integration_id").GetString());
        Assert.Equal("netbox", co.GetProperty("integration_name").GetString());
        Assert.Equal(actionId.ToString(), co.GetProperty("action_id").GetString());
        Assert.Equal("GET", co.GetProperty("method").GetString());
        Assert.Equal("/dcim/devices", co.GetProperty("path").GetString());
    }

    // A placeholder in the path is a PATH param, wherever the adapter put it.
    [Fact]
    public void Resolved_PathPlaceholdersGoToParams()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"id":"42","name":"rtr-1"}}""",
            Guid.NewGuid(), "GET", "/dcim/devices/{id}");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("42", co.GetProperty("params").GetProperty("id").GetString());
        Assert.Equal("rtr-1", co.GetProperty("query").GetProperty("name").GetString());
    }

    // For a read verb the leftovers are query string...
    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    public void Resolved_ReadVerbsRouteLeftoversToQuery(string method)
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"limit":50}}""",
            Guid.NewGuid(), method, "/devices");

        var co = node.GetProperty("config_overrides");
        Assert.Equal(50, co.GetProperty("query").GetProperty("limit").GetInt32());
        Assert.False(co.TryGetProperty("body", out _));
    }

    // ...and for a write verb they belong in the body.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public void Resolved_WriteVerbsRouteLeftoversToBody(string method)
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"name":"new-device"}}""",
            Guid.NewGuid(), method, "/devices");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("new-device", co.GetProperty("body").GetProperty("name").GetString());
        Assert.False(co.TryGetProperty("query", out _));
    }

    // DOC/CODE DIVERGENCE, pinned as-is: the method's comment says
    // "For POST/PUT/PATCH (and unknown methods), the rest go to `body`", but
    // `sendsBody` only matches the three named verbs — so an unknown verb
    // actually routes to `query`. Unknown verbs are rare enough that this has
    // not bitten anyone; the test documents what the code does today and goes
    // red if the routing is ever aligned with the comment.
    [Fact]
    public void Resolved_UnknownVerbRoutesToQueryNotBody()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"x":1}}""",
            Guid.NewGuid(), "PROPFIND", "/devices");

        var co = node.GetProperty("config_overrides");
        Assert.Equal(1, co.GetProperty("query").GetProperty("x").GetInt32());
        Assert.False(co.TryGetProperty("body", out _));
    }

    // An author's prior manual edit is authoritative and must survive.
    [Fact]
    public void Resolved_AnExistingBodyIsKeptVerbatim()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"body":{"hand":"written"},"extra":1}}""",
            Guid.NewGuid(), "POST", "/devices");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("written", co.GetProperty("body").GetProperty("hand").GetString());
    }

    [Fact]
    public void Resolved_ExistingQueryEntriesArePreserved()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"query":{"page":2}}}""",
            Guid.NewGuid(), "GET", "/devices");

        Assert.Equal(2, node.GetProperty("config_overrides").GetProperty("query").GetProperty("page").GetInt32());
    }

    // An existing `params` entry is re-routed, so a value the adapter put in
    // params that is NOT a path placeholder still lands in the right bucket.
    [Fact]
    public void Resolved_ExistingParamsAreReRouted()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"params":{"id":"42","limit":10}}}""",
            Guid.NewGuid(), "GET", "/devices/{id}");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("42", co.GetProperty("params").GetProperty("id").GetString());
        Assert.Equal(10, co.GetProperty("query").GetProperty("limit").GetInt32());
    }

    // Display-only keys stay at the root so the editor card keeps its subtitle.
    [Theory]
    [InlineData("action_name")]
    [InlineData("summary")]
    [InlineData("description")]
    [InlineData("task_name")]
    [InlineData("view")]
    [InlineData("scheduled")]
    public void Resolved_DisplayOnlyKeysStayAtTheRoot(string key)
    {
        var node = WriteResolvedNode(
            "{\"id\":\"n1\",\"config_overrides\":{\"" + key + "\":\"value\"}}",
            Guid.NewGuid(), "GET", "/devices");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("value", co.GetProperty(key).GetString());
        Assert.False(co.TryGetProperty("query", out _));   // not routed into a bucket
    }

    // Empty buckets are omitted so config_overrides doesn't fill with noise.
    [Fact]
    public void Resolved_EmptyBucketsAreOmitted()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{}}""",
            Guid.NewGuid(), "GET", "/devices");

        var co = node.GetProperty("config_overrides");
        Assert.False(co.TryGetProperty("params", out _));
        Assert.False(co.TryGetProperty("query", out _));
        Assert.False(co.TryGetProperty("body", out _));
    }

    // The node's own fields (id, position, …) survive the rewrite.
    [Fact]
    public void Resolved_NonConfigNodeFieldsSurvive()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","snippet_id":"integration_action","x":10,"y":20,"config_overrides":{}}""",
            Guid.NewGuid(), "GET", "/devices");

        Assert.Equal("n1", node.GetProperty("id").GetString());
        Assert.Equal(10, node.GetProperty("x").GetInt32());
        Assert.Equal(20, node.GetProperty("y").GetInt32());
    }

    // The stale method/path the import carried are replaced by the resolved
    // ones, never duplicated.
    [Fact]
    public void Resolved_StaleMethodAndPathAreReplaced()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{"method":"POST","path":"/old","action_id":"STALE"}}""",
            Guid.NewGuid(), "GET", "/new");

        var co = node.GetProperty("config_overrides");
        Assert.Equal("GET", co.GetProperty("method").GetString());
        Assert.Equal("/new", co.GetProperty("path").GetString());
        Assert.NotEqual("STALE", co.GetProperty("action_id").GetString());
    }

    [Fact]
    public void Resolved_NullIntegrationNameIsOmitted()
    {
        var node = WriteResolvedNode(
            """{"id":"n1","config_overrides":{}}""",
            Guid.NewGuid(), "GET", "/devices", integrationName: null);

        Assert.False(node.GetProperty("config_overrides").TryGetProperty("integration_name", out _));
    }

    // A node with no config_overrides at all still gets a valid resolved one.
    [Fact]
    public void Resolved_NodeWithoutConfigOverridesGetsAValidOne()
    {
        var actionId = Guid.NewGuid();

        var node = WriteResolvedNode("""{"id":"n1"}""", actionId, "GET", "/devices");

        Assert.Equal(actionId.ToString(),
            node.GetProperty("config_overrides").GetProperty("action_id").GetString());
    }
}
