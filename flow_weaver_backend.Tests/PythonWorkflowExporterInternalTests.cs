using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Services.Compiler;

namespace flow_weaver_backend.Tests;

// The Python exporter's internals. Two properties carry the weight: the
// generated identifiers and string literals must be valid Python (a node id
// like "ssh-show-version" is not a legal identifier, and an unescaped quote
// breaks the whole script), and the topological sort must emit EVERY node —
// including ones in a cycle — so the user still sees each step rather than
// silently getting a truncated script.
public class PythonWorkflowExporterInternalTests
{
    private static readonly Type Exporter = typeof(PythonWorkflowExporter);

    private static MethodInfo Method(string name)
        => Exporter.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    // ─── SafeIdentifier ─────────────────────────────────────────────────

    private static string SafeIdentifier(string id)
        => (string)Method("SafeIdentifier").Invoke(null, new object?[] { id })!;

    // Node ids routinely contain hyphens and dots; Python identifiers cannot.
    [Theory]
    [InlineData("ssh-show-version", "ssh_show_version")]
    [InlineData("node.one", "node_one")]
    [InlineData("a b c", "a_b_c")]
    [InlineData("__start__", "__start__")]
    [InlineData("plain", "plain")]
    public void SafeIdentifier_ReplacesEveryIllegalCharacter(string id, string expected)
    {
        Assert.Equal(expected, SafeIdentifier(id));
    }

    // A leading digit is legal in a node id but not in a Python identifier.
    [Theory]
    [InlineData("1node", "n_1node")]
    [InlineData("2", "n_2")]
    public void SafeIdentifier_PrefixesLeadingDigits(string id, string expected)
    {
        Assert.Equal(expected, SafeIdentifier(id));
    }

    [Fact]
    public void SafeIdentifier_EmptyIdStillYieldsSomethingUsable()
    {
        Assert.Equal("n_", SafeIdentifier(""));
    }

    // Everything it produces must actually be a legal Python identifier.
    [Theory]
    [InlineData("node/with/slashes")]
    [InlineData("héllo-wörld")]
    [InlineData("!@#$%")]
    [InlineData("3-tier")]
    public void SafeIdentifier_OutputIsAlwaysLegal(string id)
    {
        var result = SafeIdentifier(id);

        Assert.False(char.IsDigit(result[0]));
        Assert.All(result, c => Assert.True(char.IsLetterOrDigit(c) || c == '_', $"illegal char '{c}'"));
    }

    // ─── PyString / Escape ──────────────────────────────────────────────

    private static string PyString(string s)
        => (string)Method("PyString").Invoke(null, new object?[] { s })!;

    [Fact]
    public void PyString_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"hello\"", PyString("hello"));
    }

    // An unescaped quote would terminate the literal and break the script.
    [Fact]
    public void PyString_EscapesEmbeddedQuotes()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", PyString("say \"hi\""));
    }

    // Backslashes must be escaped BEFORE quotes, or a trailing backslash
    // escapes the closing quote.
    [Fact]
    public void PyString_EscapesBackslashes()
    {
        Assert.Equal("\"C:\\\\path\"", PyString(@"C:\path"));
    }

    [Fact]
    public void PyString_ABackslashBeforeAQuoteDoesNotEscapeTheDelimiter()
    {
        // Input: a\"  →  the backslash and the quote must both be escaped.
        var result = PyString("a\\\"");

        Assert.Equal("\"a\\\\\\\"\"", result);
    }

    // A newline inside a single-quoted Python literal is a syntax error.
    [Fact]
    public void PyString_EscapesNewlines()
    {
        Assert.Equal("\"line1\\nline2\"", PyString("line1\nline2"));
    }

    // CRLF input must not leave a stray carriage return in the literal.
    [Fact]
    public void PyString_DropsCarriageReturns()
    {
        Assert.Equal("\"line1\\nline2\"", PyString("line1\r\nline2"));
    }

    [Fact]
    public void PyString_EmptyStringIsAValidLiteral()
    {
        Assert.Equal("\"\"", PyString(""));
    }

    // ─── ParseNodes ─────────────────────────────────────────────────────

    private static List<object> ParseNodes(string json)
        => ((System.Collections.IEnumerable)Method("ParseNodes")
            .Invoke(null, new object?[] { TestJson.Element(json) })!)
            .Cast<object>().ToList();

    private static (string Id, string SnippetId) NodeOf(object node)
    {
        var type = node.GetType();
        return ((string)type.GetProperty("Id")!.GetValue(node)!,
                (string)type.GetProperty("SnippetId")!.GetValue(node)!);
    }

    [Fact]
    public void ParseNodes_ReadsIdAndSnippetId()
    {
        var nodes = ParseNodes("""[{"id":"n1","snippet_id":"s1"}]""");

        var (id, snippetId) = NodeOf(Assert.Single(nodes));
        Assert.Equal("n1", id);
        Assert.Equal("s1", snippetId);
    }

    // A node without an id cannot be emitted as a function, so it is skipped.
    [Theory]
    [InlineData("""[{"snippet_id":"s1"}]""")]
    [InlineData("""[{"id":""}]""")]
    [InlineData("""[{"id":null}]""")]
    public void ParseNodes_SkipsNodesWithoutAnId(string json)
    {
        Assert.Empty(ParseNodes(json));
    }

    [Fact]
    public void ParseNodes_MissingSnippetIdBecomesEmpty()
    {
        var (_, snippetId) = NodeOf(Assert.Single(ParseNodes("""[{"id":"n1"}]""")));

        Assert.Equal("", snippetId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"not":"an array"}""")]
    public void ParseNodes_NonArrayYieldsNothing(string json)
    {
        Assert.Empty(ParseNodes(json));
    }

    // ─── ParseEdges ─────────────────────────────────────────────────────

    private static List<object> ParseEdges(string json)
        => ((System.Collections.IEnumerable)Method("ParseEdges")
            .Invoke(null, new object?[] { TestJson.Element(json) })!)
            .Cast<object>().ToList();

    private static (string Source, string Target, string Type) EdgeOf(object edge)
    {
        var type = edge.GetType();
        return ((string)type.GetProperty("Source")!.GetValue(edge)!,
                (string)type.GetProperty("Target")!.GetValue(edge)!,
                (string)type.GetProperty("Type")!.GetValue(edge)!);
    }

    [Fact]
    public void ParseEdges_ReadsTheTriple()
    {
        var edges = ParseEdges("""[{"source":"a","target":"b","type":"failure"}]""");

        var (source, target, type) = EdgeOf(Assert.Single(edges));
        Assert.Equal("a", source);
        Assert.Equal("b", target);
        Assert.Equal("failure", type);
    }

    [Fact]
    public void ParseEdges_TypeDefaultsToSuccess()
    {
        var (_, _, type) = EdgeOf(Assert.Single(ParseEdges("""[{"source":"a","target":"b"}]""")));

        Assert.Equal("success", type);
    }

    // A half-formed edge cannot be wired, so it is dropped rather than
    // producing a dangling reference in the script.
    [Theory]
    [InlineData("""[{"target":"b"}]""")]
    [InlineData("""[{"source":"a"}]""")]
    [InlineData("""[{"source":"","target":"b"}]""")]
    public void ParseEdges_SkipsIncompleteEdges(string json)
    {
        Assert.Empty(ParseEdges(json));
    }

    // ─── TopoSort ───────────────────────────────────────────────────────

    private static List<string> TopoSortIds(string nodesJson, string edgesJson)
    {
        var nodes = Method("ParseNodes").Invoke(null, new object?[] { TestJson.Element(nodesJson) })!;
        var edges = Method("ParseEdges").Invoke(null, new object?[] { TestJson.Element(edgesJson) })!;
        var sorted = (System.Collections.IEnumerable)Method("TopoSort").Invoke(null, new[] { nodes, edges })!;
        return sorted.Cast<object>().Select(n => NodeOf(n).Id).ToList();
    }

    [Fact]
    public void TopoSort_OrdersALinearChain()
    {
        var order = TopoSortIds(
            """[{"id":"c"},{"id":"a"},{"id":"b"}]""",
            """[{"source":"a","target":"b"},{"source":"b","target":"c"}]""");

        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void TopoSort_RespectsADiamond()
    {
        var order = TopoSortIds(
            """[{"id":"a"},{"id":"b"},{"id":"c"},{"id":"d"}]""",
            """
            [{"source":"a","target":"b"},{"source":"a","target":"c"},
             {"source":"b","target":"d"},{"source":"c","target":"d"}]
            """);

        Assert.Equal("a", order[0]);
        Assert.Equal("d", order[^1]);
        Assert.Equal(4, order.Count);
    }

    // Isolated nodes are still steps the user authored; dropping them would
    // silently shorten the exported script.
    [Fact]
    public void TopoSort_KeepsIsolatedNodes()
    {
        var order = TopoSortIds(
            """[{"id":"a"},{"id":"orphan"},{"id":"b"}]""",
            """[{"source":"a","target":"b"}]""");

        Assert.Equal(3, order.Count);
        Assert.Contains("orphan", order);
    }

    // A cycle has no valid ordering, but the nodes must still appear — the
    // user needs to see the steps to spot the loop.
    [Fact]
    public void TopoSort_EmitsCycleMembersRatherThanDroppingThem()
    {
        var order = TopoSortIds(
            """[{"id":"a"},{"id":"b"},{"id":"c"}]""",
            """
            [{"source":"a","target":"b"},{"source":"b","target":"c"},
             {"source":"c","target":"a"}]
            """);

        Assert.Equal(3, order.Count);
        Assert.Equal(new[] { "a", "b", "c" }, order.OrderBy(x => x, StringComparer.Ordinal));
    }

    // Every node appears exactly once, whatever the graph.
    [Fact]
    public void TopoSort_NeverDuplicatesANode()
    {
        var order = TopoSortIds(
            """[{"id":"a"},{"id":"b"},{"id":"c"}]""",
            """
            [{"source":"a","target":"b"},{"source":"a","target":"c"},
             {"source":"b","target":"c"},{"source":"a","target":"c"}]
            """);

        Assert.Equal(order.Count, order.Distinct().Count());
        Assert.Equal(3, order.Count);
    }

    // Edges pointing at nodes that don't exist must not throw — imports carry
    // dangling references all the time.
    [Fact]
    public void TopoSort_IgnoresEdgesToUnknownNodes()
    {
        var order = TopoSortIds(
            """[{"id":"a"},{"id":"b"}]""",
            """
            [{"source":"a","target":"b"},{"source":"a","target":"ghost"},
             {"source":"ghost","target":"b"}]
            """);

        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void TopoSort_EmptyGraphYieldsNothing()
    {
        Assert.Empty(TopoSortIds("[]", "[]"));
    }

    [Fact]
    public void TopoSort_NoEdgesKeepsInputOrder()
    {
        var order = TopoSortIds("""[{"id":"c"},{"id":"a"},{"id":"b"}]""", "[]");

        Assert.Equal(new[] { "c", "a", "b" }, order);
    }
}
