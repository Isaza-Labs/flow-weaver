using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Services.Import.Models;

namespace flow_weaver_backend.Tests;

// The pure helpers inside WorkflowImportController. Imports arrive from our own
// exporter, n8n, Itential and the agent, each with its own dialect, so these
// sanitisers are what stand between a foreign payload and a schema rejection —
// or worse, a workflow that validates but references nodes that no longer
// exist. Reached by reflection (private statics on a controller).
public class WorkflowImportControllerHelperTests
{
    private static readonly Type Controller = typeof(WorkflowImportController);

    private static MethodInfo Method(string name)
        => Controller.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    private static T Invoke<T>(string name, params object?[] args)
        => (T)Method(name).Invoke(null, args)!;

    // ─── NormalizeNodesForV1Schema ──────────────────────────────────────

    private static JsonElement NormalizeNodes(string json)
        => Invoke<JsonElement>("NormalizeNodesForV1Schema", TestJson.Element(json));

    // The v1 schema sets additionalProperties:false, so foreign keys must be
    // dropped rather than rejected.
    [Fact]
    public void NormalizeNodes_DropsForeignKeys()
    {
        var result = NormalizeNodes("""
            [{"id":"a","snippet_id":"s1","x":10,"y":20,
              "label":"pretty","data":{"n8n":true},"position":{"x":1},"selected":true}]
            """);

        var node = result[0];
        Assert.Equal("a", node.GetProperty("id").GetString());
        Assert.Equal("s1", node.GetProperty("snippet_id").GetString());
        Assert.Equal(10, node.GetProperty("x").GetInt32());
        Assert.False(node.TryGetProperty("label", out _));
        Assert.False(node.TryGetProperty("data", out _));
        Assert.False(node.TryGetProperty("position", out _));
    }

    // An out-of-enum type is dropped so the schema's default ("task") applies —
    // this is what makes `start`/`end` nodes from other tools importable.
    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("whatever")]
    public void NormalizeNodes_DropsOutOfEnumTypes(string type)
    {
        var result = NormalizeNodes($$"""[{"id":"a","type":"{{type}}"}]""");

        Assert.False(result[0].TryGetProperty("type", out _));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("decision")]
    [InlineData("subflow")]
    public void NormalizeNodes_KeepsValidTypes(string type)
    {
        var result = NormalizeNodes($$"""[{"id":"a","type":"{{type}}"}]""");

        Assert.Equal(type, result[0].GetProperty("type").GetString());
    }

    // A non-object config_overrides (null from a YAML round-trip) becomes an
    // empty object so the schema's type constraint holds.
    [Theory]
    [InlineData("null")]
    [InlineData("\"string\"")]
    [InlineData("[]")]
    public void NormalizeNodes_NonObjectConfigOverridesBecomeEmptyObject(string value)
    {
        var result = NormalizeNodes($$"""[{"id":"a","config_overrides":{{value}}}]""");

        var co = result[0].GetProperty("config_overrides");
        Assert.Equal(JsonValueKind.Object, co.ValueKind);
        Assert.Empty(co.EnumerateObject());
    }

    [Fact]
    public void NormalizeNodes_KeepsObjectConfigOverridesVerbatim()
    {
        var result = NormalizeNodes("""[{"id":"a","config_overrides":{"cmd":"show ver","n":1}}]""");

        var co = result[0].GetProperty("config_overrides");
        Assert.Equal("show ver", co.GetProperty("cmd").GetString());
        Assert.Equal(1, co.GetProperty("n").GetInt32());
    }

    // Wrong-typed values for known keys are dropped rather than forwarded as
    // the wrong JSON type.
    [Fact]
    public void NormalizeNodes_DropsWrongTypedKnownKeys()
    {
        var result = NormalizeNodes("""[{"id":123,"snippet_id":null,"x":"ten","y":20}]""");

        var node = result[0];
        Assert.False(node.TryGetProperty("id", out _));
        Assert.False(node.TryGetProperty("snippet_id", out _));
        Assert.False(node.TryGetProperty("x", out _));
        Assert.Equal(20, node.GetProperty("y").GetInt32());
    }

    [Fact]
    public void NormalizeNodes_NonArrayInputIsReturnedUnchanged()
    {
        var result = NormalizeNodes("""{"not":"an array"}""");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    [Fact]
    public void NormalizeNodes_NonObjectEntriesBecomeEmptyObjects()
    {
        var result = NormalizeNodes("""["just a string", 42]""");

        Assert.Equal(2, result.GetArrayLength());
        Assert.All(result.EnumerateArray(), e => Assert.Equal(JsonValueKind.Object, e.ValueKind));
    }

    // ─── NormalizeEdgesForV1Schema ──────────────────────────────────────

    private static JsonElement NormalizeEdges(string json)
        => Invoke<JsonElement>("NormalizeEdgesForV1Schema", TestJson.Element(json));

    [Fact]
    public void NormalizeEdges_KeepsTheSchemaFieldsAndDropsTheRest()
    {
        var result = NormalizeEdges("""
            [{"source":"a","target":"b","type":"success","animated":true,"id":"e1"}]
            """);

        var edge = result[0];
        Assert.Equal("a", edge.GetProperty("source").GetString());
        Assert.Equal("b", edge.GetProperty("target").GetString());
        Assert.Equal("success", edge.GetProperty("type").GetString());
        Assert.False(edge.TryGetProperty("animated", out _));
        Assert.False(edge.TryGetProperty("id", out _));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("always")]
    [InlineData("conditional")]
    public void NormalizeEdges_KeepsValidTypes(string type)
    {
        var result = NormalizeEdges($$"""[{"source":"a","target":"b","type":"{{type}}"}]""");

        Assert.Equal(type, result[0].GetProperty("type").GetString());
    }

    [Fact]
    public void NormalizeEdges_DropsOutOfEnumTypes()
    {
        var result = NormalizeEdges("""[{"source":"a","target":"b","type":"smoothstep"}]""");

        Assert.False(result[0].TryGetProperty("type", out _));
    }

    // A YAML exporter writing `condition:` with no value produces null; the
    // schema wants a string, so nulls and empties must be dropped to keep the
    // field genuinely optional.
    [Theory]
    [InlineData("condition", "null")]
    [InlineData("condition", "\"\"")]
    [InlineData("source_handle", "null")]
    [InlineData("target_handle", "null")]
    [InlineData("condition", "123")]
    public void NormalizeEdges_DropsNullOrEmptyOptionalStrings(string key, string value)
    {
        var result = NormalizeEdges($$"""[{"source":"a","target":"b","{{key}}":{{value}}}]""");

        Assert.False(result[0].TryGetProperty(key, out _));
    }

    [Fact]
    public void NormalizeEdges_KeepsPopulatedOptionalStrings()
    {
        var result = NormalizeEdges("""
            [{"source":"a","target":"b","condition":"x > 1","source_handle":"out"}]
            """);

        Assert.Equal("x > 1", result[0].GetProperty("condition").GetString());
        Assert.Equal("out", result[0].GetProperty("source_handle").GetString());
    }

    [Fact]
    public void NormalizeEdges_NonArrayInputIsReturnedUnchanged()
    {
        Assert.Equal(JsonValueKind.Null, NormalizeEdges("null").ValueKind);
    }

    // ─── DropSkippedNodes ───────────────────────────────────────────────

    private static JsonElement DropSkipped(string workflow, HashSet<string> skipped, List<string> warnings)
        => Invoke<JsonElement>("DropSkippedNodes", TestJson.Element(workflow), skipped, warnings);

    private const string TwoNodeWorkflow = """
        {"name":"wf",
         "nodes":[{"id":"n1","snippet_id":"s-keep"},{"id":"n2","snippet_id":"s-skip"}],
         "edges":[{"source":"n1","target":"n2"},{"source":"n2","target":"n3"},
                  {"source":"n1","target":"n3"}]}
        """;

    // Dropping a node must take its edges with it — a dangling edge produces a
    // workflow that validates but cannot run.
    [Fact]
    public void DropSkipped_RemovesTheNodeAndEveryEdgeTouchingIt()
    {
        var warnings = new List<string>();

        var result = DropSkipped(TwoNodeWorkflow, new HashSet<string> { "s-skip" }, warnings);

        var nodeIds = result.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "n1" }, nodeIds);

        var edges = result.GetProperty("edges").EnumerateArray().ToList();
        Assert.Single(edges);
        Assert.Equal("n3", edges[0].GetProperty("target").GetString());
    }

    [Fact]
    public void DropSkipped_WarnsAboutEachDroppedNode()
    {
        var warnings = new List<string>();

        DropSkipped(TwoNodeWorkflow, new HashSet<string> { "s-skip" }, warnings);

        var warning = Assert.Single(warnings);
        Assert.Contains("n2", warning);
        Assert.Contains("s-skip", warning);
    }

    [Fact]
    public void DropSkipped_PreservesOtherTopLevelKeys()
    {
        var result = DropSkipped(TwoNodeWorkflow, new HashSet<string> { "s-skip" }, new List<string>());

        Assert.Equal("wf", result.GetProperty("name").GetString());
    }

    [Fact]
    public void DropSkipped_NothingSkippedIsANoOp()
    {
        var warnings = new List<string>();

        var result = DropSkipped(TwoNodeWorkflow, new HashSet<string>(), warnings);

        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
        Assert.Empty(warnings);
    }

    // A skip id that matches no node changes nothing.
    [Fact]
    public void DropSkipped_UnmatchedSkipIdIsANoOp()
    {
        var warnings = new List<string>();

        var result = DropSkipped(TwoNodeWorkflow, new HashSet<string> { "s-nonexistent" }, warnings);

        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
        Assert.Empty(warnings);
    }

    [Fact]
    public void DropSkipped_NonObjectWorkflowIsReturnedUnchanged()
    {
        var result = DropSkipped("[]", new HashSet<string> { "s" }, new List<string>());

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
    }

    // ─── ReadWrappedString / TryGetString ───────────────────────────────

    private static string? ReadWrapped(string json, string key)
        => Invoke<string?>("ReadWrappedString", TestJson.Element(json), key)!;

    // Our own exporter nests under `workflow:`; foreign translators use the top
    // level. Both must round-trip the title.
    [Fact]
    public void ReadWrapped_PrefersTheWorkflowWrapper()
    {
        Assert.Equal("nested", ReadWrapped("""{"workflow":{"name":"nested"},"name":"top"}""", "name"));
    }

    [Fact]
    public void ReadWrapped_FallsBackToTheTopLevel()
    {
        Assert.Equal("top", ReadWrapped("""{"name":"top"}""", "name"));
    }

    // An empty nested value falls through rather than winning.
    [Fact]
    public void ReadWrapped_EmptyNestedValueFallsBackToTheTopLevel()
    {
        Assert.Equal("top", ReadWrapped("""{"workflow":{"name":"  "},"name":"top"}""", "name"));
    }

    [Theory]
    [InlineData("""{"other":"x"}""")]
    [InlineData("""{"name":123}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("[]")]
    public void ReadWrapped_MissingOrNonStringYieldsNull(string json)
    {
        Assert.Null(ReadWrapped(json, "name"));
    }

    // ─── ResolveName / CopySuffix ───────────────────────────────────────

    private static string ResolveName(string workflow, string conflictRes, string? newName, NameCollision? collision)
        => Invoke<string>("ResolveName", TestJson.Element(workflow), conflictRes, newName, collision);

    private static NameCollision SomeCollision() => new()
    {
        MatchingWorkflowId = Guid.NewGuid(),
        MatchingWorkflowEnvironment = "draft",
    };

    [Fact]
    public void ResolveName_KeepsTheOriginalByDefault()
    {
        Assert.Equal("my flow", ResolveName("""{"name":"my flow"}""", "overwrite", null, null));
    }

    [Fact]
    public void ResolveName_FallsBackWhenTheImportHasNoName()
    {
        Assert.Equal("Imported workflow", ResolveName("{}", "overwrite", null, null));
    }

    [Fact]
    public void ResolveName_RenameUsesTheSuppliedName()
    {
        Assert.Equal("chosen", ResolveName("""{"name":"orig"}""", "rename", "chosen", null));
    }

    // Rename without a name still has to produce something unique.
    [Fact]
    public void ResolveName_RenameWithoutANameFallsBackToACopySuffix()
    {
        var name = ResolveName("""{"name":"orig"}""", "rename", "  ", null);

        Assert.StartsWith("orig (copy ", name);
    }

    [Fact]
    public void ResolveName_FreshCopyOnlySuffixesWhenThereIsACollision()
    {
        Assert.StartsWith("orig (copy ",
            ResolveName("""{"name":"orig"}""", "fresh_copy", null, SomeCollision()));
        Assert.Equal("orig",
            ResolveName("""{"name":"orig"}""", "fresh_copy", null, null));
    }

    // ─── InferHttpMethodFromActionName ──────────────────────────────────

    private static string InferMethod(string actionName)
        => Invoke<string>("InferHttpMethodFromActionName", actionName);

    [Theory]
    [InlineData("getDevices", "GET")]
    [InlineData("list_sites", "GET")]
    [InlineData("findByName", "GET")]
    [InlineData("searchPrefixes", "GET")]
    [InlineData("createDevice", "POST")]
    [InlineData("addMember", "POST")]
    [InlineData("sendMessage", "POST")]
    [InlineData("triggerJob", "POST")]
    [InlineData("updateDevice", "PATCH")]
    [InlineData("setStatus", "PATCH")]
    [InlineData("editRecord", "PATCH")]
    [InlineData("replaceConfig", "PUT")]
    [InlineData("putObject", "PUT")]
    [InlineData("deleteDevice", "DELETE")]
    [InlineData("removeMember", "DELETE")]
    [InlineData("purgeCache", "DELETE")]
    public void InferMethod_MapsVerbPrefixes(string actionName, string expected)
    {
        Assert.Equal(expected, InferMethod(actionName));
    }

    // Matching is case-insensitive on the action name.
    [Theory]
    [InlineData("GetDevices", "GET")]
    [InlineData("DELETEDevice", "DELETE")]
    public void InferMethod_IsCaseInsensitive(string actionName, string expected)
    {
        Assert.Equal(expected, InferMethod(actionName));
    }

    // An unrecognisable name defaults to the safest verb.
    [Theory]
    [InlineData("frobnicate")]
    [InlineData("")]
    [InlineData("xyzzy_thing")]
    public void InferMethod_DefaultsToGet(string actionName)
    {
        Assert.Equal("GET", InferMethod(actionName));
    }

    // ─── ParsePathParamNames ────────────────────────────────────────────

    private static HashSet<string> ParsePathParams(string path)
        => Invoke<HashSet<string>>("ParsePathParamNames", path);

    [Fact]
    public void ParsePathParams_ExtractsEveryPlaceholder()
    {
        var names = ParsePathParams("/dcim/devices/{id}/interfaces/{if_name}");

        Assert.Equal(new[] { "id", "if_name" }, names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/dcim/devices")]
    [InlineData("/broken/{unclosed")]
    [InlineData("/empty/{}")]
    public void ParsePathParams_NoPlaceholdersYieldsAnEmptySet(string path)
    {
        Assert.Empty(ParsePathParams(path));
    }

    [Fact]
    public void ParsePathParams_DeduplicatesRepeatedNames()
    {
        Assert.Single(ParsePathParams("/a/{id}/b/{id}"));
    }

    // ─── string similarity (fuzzy action matching) ──────────────────────

    private static int Levenshtein(string a, string b)
        => Invoke<int>("Levenshtein", a, b);

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    public void Levenshtein_ComputesEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, Levenshtein(a, b));
    }

    private static double NameSimilarity(string a, string b)
        => Invoke<double>("NameSimilarity", a, b);

    [Fact]
    public void NameSimilarity_IdenticalStringsScoreOne()
    {
        Assert.Equal(1.0, NameSimilarity("getDevices", "getDevices"));
    }

    // Case differences must not cost similarity — importers vary in casing.
    [Fact]
    public void NameSimilarity_IsCaseInsensitive()
    {
        Assert.Equal(1.0, NameSimilarity("GetDevices", "getdevices"));
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", "")]
    public void NameSimilarity_EmptyInputScoresZero(string a, string b)
    {
        Assert.Equal(0.0, NameSimilarity(a, b));
    }

    [Fact]
    public void NameSimilarity_IsBoundedAndOrdered()
    {
        var close = NameSimilarity("getDevices", "getDevice");
        var far = NameSimilarity("getDevices", "purgeCache");

        Assert.InRange(close, 0.0, 1.0);
        Assert.InRange(far, 0.0, 1.0);
        Assert.True(close > far);
    }

    private static string ExtractPathTail(string path)
        => Invoke<string>("ExtractPathTail", path);

    [Theory]
    [InlineData("/api/dcim/devices", "devices")]
    [InlineData("/api/dcim/devices/", "devices")]
    [InlineData("devices", "devices")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void ExtractPathTail_TakesTheLastSegment(string path, string expected)
    {
        Assert.Equal(expected, ExtractPathTail(path));
    }

    private static double ScoreActionMatch(string actionName, string name, string path, string? description)
        => Invoke<double>("ScoreActionMatch", actionName, name, path, description);

    // An exact name match must clear the caller's 0.8 auto-apply threshold.
    [Fact]
    public void ScoreActionMatch_ExactNameClearsTheAutoApplyThreshold()
    {
        var score = ScoreActionMatch("getDevices", "getDevices", "/api/dcim/devices", null);

        Assert.True(score >= 0.8, $"expected >= 0.8 but was {score}");
    }

    // An unrelated candidate must stay well below it.
    [Fact]
    public void ScoreActionMatch_UnrelatedCandidateScoresLow()
    {
        var score = ScoreActionMatch("getDevices", "purgeAuditLog", "/api/audit/purge", "removes old rows");

        Assert.True(score < 0.8, $"expected < 0.8 but was {score}");
    }

    // The path tail is a real signal: "getDevices" vs an unhelpfully-named
    // action on /api/dcim/devices.
    [Fact]
    public void ScoreActionMatch_PathTailContributes()
    {
        var withPath = ScoreActionMatch("devices", "op_1", "/api/dcim/devices", null);
        var withoutPath = ScoreActionMatch("devices", "op_1", "", null);

        Assert.True(withPath > withoutPath);
    }

    [Fact]
    public void ScoreActionMatch_IsBoundedToTheUnitInterval()
    {
        Assert.InRange(ScoreActionMatch("a", "a", "/a", "a"), 0.0, 1.0);
        Assert.InRange(ScoreActionMatch("", "", "", null), 0.0, 1.0);
    }

    // ─── TryGetElement ──────────────────────────────────────────────────

    private static JsonElement TryGetElement(string json, string key)
        => Invoke<JsonElement>("TryGetElement", TestJson.Element(json), key);

    [Fact]
    public void TryGetElement_ReturnsADetachedClone()
    {
        JsonElement value;
        using (var doc = JsonDocument.Parse("""{"cfg":{"a":1}}"""))
        {
            value = Invoke<JsonElement>("TryGetElement", doc.RootElement, "cfg");
        }

        Assert.Equal(1, value.GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData("""{"other":1}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void TryGetElement_MissingKeyOrNonObjectYieldsUndefined(string json)
    {
        Assert.Equal(JsonValueKind.Undefined, TryGetElement(json, "cfg").ValueKind);
    }
}
