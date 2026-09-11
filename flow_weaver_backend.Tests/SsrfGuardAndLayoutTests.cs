using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The SSRF guard, the graph auto-layout and the model token budget.
//
// UrlGuard is the highest-stakes piece in the file: every outbound URL the
// agent or a workflow can influence passes through it, and the cloud-metadata
// address is the single worst target — so `allowPrivate` must NOT open it.
public class SsrfGuardAndLayoutTests
{
    // ─── UrlGuard ───────────────────────────────────────────────────────

    private static UrlGuard Guard(bool allowInternal = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowInternalUrls"] = allowInternal ? "true" : "false",
            })
            .Build();
        return new UrlGuard(config, NullLogger<UrlGuard>.Instance);
    }

    private static void AssertBlocked(string url, bool allowPrivate = false)
        => Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe(url, allowPrivate));

    // Hostnames that must never resolve at all — `::` and `0.0.0.0` in
    // particular throw inside DNS, so they are caught by name first.
    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("http://LOCALHOST/admin")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    public void Guard_BlocksLoopbackAndUnspecifiedHosts(string url)
    {
        AssertBlocked(url);
    }

    // The cloud metadata endpoint: reachable from inside almost every VM and
    // hands out credentials.
    [Fact]
    public void Guard_BlocksTheCloudMetadataAddress()
    {
        AssertBlocked("http://169.254.169.254/latest/meta-data/");
    }

    // …and `allowPrivate` must NOT open it. An integration marked
    // "internal network" is asking for RFC-1918, not for link-local.
    [Fact]
    public void Guard_AllowPrivateDoesNotOpenLinkLocal()
    {
        AssertBlocked("http://169.254.169.254/latest/meta-data/", allowPrivate: true);
        AssertBlocked("http://169.254.1.1/", allowPrivate: true);
    }

    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.0.5/")]
    [InlineData("http://172.31.255.1/")]
    [InlineData("http://192.168.1.1/")]
    public void Guard_BlocksRfc1918ByDefault(string url)
    {
        AssertBlocked(url);
    }

    // The opt-in exists so an on-prem integration can be reached at all.
    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.0.5/")]
    [InlineData("http://192.168.1.1/")]
    public void Guard_AllowPrivateOpensRfc1918(string url)
    {
        Guard().EnsureSafe(url, allowPrivate: true);
    }

    // 172.15 and 172.32 are outside the /12 — blocking them would break
    // legitimate public addresses.
    [Theory]
    [InlineData("http://172.15.0.1/")]
    [InlineData("http://172.32.0.1/")]
    public void Guard_AddressesOutsideThe172RangeAreNotPrivate(string url)
    {
        Guard().EnsureSafe(url);
    }

    [Fact]
    public void Guard_Blocks0Slash8()
    {
        AssertBlocked("http://0.1.2.3/");
    }

    [Theory]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fec0::1]/")]
    public void Guard_BlocksIPv6LinkAndSiteLocalEvenWithTheOptIn(string url)
    {
        AssertBlocked(url, allowPrivate: true);
    }

    [Fact]
    public void Guard_IPv6UniqueLocalFollowsTheOptIn()
    {
        AssertBlocked("http://[fd00::1]/");
        Guard().EnsureSafe("http://[fd00::1]/", allowPrivate: true);
    }

    // An IPv4-mapped IPv6 literal is a classic smuggling trick: it has to be
    // evaluated as the embedded IPv4.
    [Fact]
    public void Guard_AnIPv4MappedIPv6LiteralIsEvaluatedAsItsIPv4()
    {
        AssertBlocked("http://[::ffff:169.254.169.254]/");
        AssertBlocked("http://[::ffff:10.0.0.1]/");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative")]
    public void Guard_AnUnparseableUrlIsBlocked(string url)
    {
        AssertBlocked(url);
    }

    // A hostname that doesn't resolve can't be checked, so it is refused
    // rather than assumed safe.
    [Fact]
    public void Guard_AnUnresolvableHostIsBlocked()
    {
        AssertBlocked("http://this-host-does-not-exist.invalid/");
    }

    // The global escape hatch exists for local development against a docker
    // compose stack; when it's on, nothing is checked.
    [Theory]
    [InlineData("http://localhost/admin")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("not a url")]
    public void Guard_TheGlobalOptOutBypassesEveryCheck(string url)
    {
        Guard(allowInternal: true).EnsureSafe(url);
    }

    // ─── WorkflowAutoLayout ─────────────────────────────────────────────

    private static JsonElement E(string json) => TestJson.Element(json);

    private static string Node(string id, double x = 0, double y = 0)
        => "{\"id\":" + JsonSerializer.Serialize(id) + ",\"x\":" + x + ",\"y\":" + y + "}";

    private static string Edge(string source, string target, bool handles = true)
        => "{\"source\":" + JsonSerializer.Serialize(source)
           + ",\"target\":" + JsonSerializer.Serialize(target)
           + (handles ? ",\"source_handle\":\"out\",\"target_handle\":\"in\"" : "")
           + "}";

    // The agent and the importers never set edge handles, so a missing one
    // is the reliable "this came from a machine, lay it out" signal.
    [Fact]
    public void Layout_AnEdgeWithoutHandlesRequestsLayout()
    {
        Assert.True(WorkflowAutoLayout.NeedsLayout(
            E("[" + Node("a", 10, 10) + "," + Node("b", 200, 10) + "]"),
            E("[" + Edge("a", "b", handles: false) + "]")));
    }

    // A user-tuned workflow has BOTH handles and distinct positions — laying
    // it out again would throw away their work.
    [Fact]
    public void Layout_AUserTunedGraphIsLeftAlone()
    {
        Assert.False(WorkflowAutoLayout.NeedsLayout(
            E("[" + Node("a", 10, 10) + "," + Node("b", 200, 90) + "]"),
            E("[" + Edge("a", "b") + "]")));
    }

    // The other telltale: everything stacked at the same coordinate.
    [Fact]
    public void Layout_EveryoneAtTheSamePointRequestsLayout()
    {
        Assert.True(WorkflowAutoLayout.NeedsLayout(
            E("[" + Node("a") + "," + Node("b") + "]"),
            E("[" + Edge("a", "b") + "]")));
    }

    // Sentinels don't count toward the "stacked" heuristic — they legitimately
    // sit at fixed coordinates.
    [Fact]
    public void Layout_ASingleTaskNodeNeedsNoLayout()
    {
        Assert.False(WorkflowAutoLayout.NeedsLayout(
            E("[" + Node("__start__") + "," + Node("a") + "," + Node("__end__") + "]"),
            E("[" + Edge("__start__", "a") + "," + Edge("a", "__end__") + "]")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public void Layout_ANonArrayGraphNeedsNoLayout(string blob)
    {
        Assert.False(WorkflowAutoLayout.NeedsLayout(E(blob), E(blob)));
    }

    // Applying layout gives every node a distinct position so the editor
    // renders a readable DAG instead of a pile.
    [Fact]
    public void Layout_ApplySpreadsTheNodesOut()
    {
        var (nodes, _) = WorkflowAutoLayout.Apply(
            E("[" + Node("a") + "," + Node("b") + "," + Node("c") + "]"),
            E("[" + Edge("a", "b") + "," + Edge("b", "c") + "]"));

        var xs = nodes.EnumerateArray().Select(n => n.GetProperty("x").GetDouble()).ToList();
        Assert.Equal(xs.Count, xs.Distinct().Count());
    }

    // Layout follows the DAG: a successor sits to the right of its
    // predecessor, which is what makes the rendering readable.
    [Fact]
    public void Layout_SuccessorsSitDownstreamOfTheirPredecessors()
    {
        var (nodes, _) = WorkflowAutoLayout.Apply(
            E("[" + Node("a") + "," + Node("b") + "]"),
            E("[" + Edge("a", "b") + "]"));

        var byId = nodes.EnumerateArray()
            .ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("x").GetDouble());
        Assert.True(byId["b"] > byId["a"]);
    }

    // Everything the caller had on a node survives the rewrite — layout only
    // touches coordinates.
    [Fact]
    public void Layout_ApplyPreservesEveryOtherNodeField()
    {
        var (nodes, _) = WorkflowAutoLayout.Apply(
            E("""[{"id":"a","x":0,"y":0,"snippet_id":"ssh","config_overrides":{"k":1}}]"""),
            E("[]"));

        var node = nodes.EnumerateArray().Single();
        Assert.Equal("ssh", node.GetProperty("snippet_id").GetString());
        Assert.Equal(1, node.GetProperty("config_overrides").GetProperty("k").GetInt32());
    }

    // Parallel branches must not land on top of each other.
    [Fact]
    public void Layout_ParallelBranchesGetDistinctRows()
    {
        var (nodes, _) = WorkflowAutoLayout.Apply(
            E("[" + Node("a") + "," + Node("b") + "," + Node("c") + "]"),
            E("[" + Edge("a", "b") + "," + Edge("a", "c") + "]"));

        var byId = nodes.EnumerateArray()
            .ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("y").GetDouble());
        Assert.NotEqual(byId["b"], byId["c"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Layout_ApplyOnAnEmptyOrNonArrayGraphIsANoOp(string blob)
    {
        var (nodes, edges) = WorkflowAutoLayout.Apply(E(blob), E("[]"));

        Assert.Equal(blob.Replace(" ", ""), nodes.GetRawText().Replace(" ", ""));
        Assert.Equal(JsonValueKind.Array, edges.ValueKind);
    }

    // An edge naming a node that isn't in the graph must not derail layout.
    [Fact]
    public void Layout_ADanglingEdgeIsIgnored()
    {
        var (nodes, _) = WorkflowAutoLayout.Apply(
            E("[" + Node("a") + "]"),
            E("[" + Edge("a", "ghost") + "]"));

        Assert.Single(nodes.EnumerateArray());
    }

    // A cycle would hang a naive longest-path ranker; layout has to
    // terminate on one.
    [Fact]
    public void Layout_ACycleStillTerminates()
    {
        var (nodes, _) = WorkflowAutoLayout.Apply(
            E("[" + Node("a") + "," + Node("b") + "]"),
            E("[" + Edge("a", "b") + "," + Edge("b", "a") + "]"));

        Assert.Equal(2, nodes.GetArrayLength());
    }

    // ─── ModelLimits ────────────────────────────────────────────────────

    [Fact]
    public void Limits_TheDefaultsAreUsable()
    {
        var limits = new ModelLimits();

        Assert.Equal(ModelLimits.DefaultContextWindowTokens, limits.ContextWindowTokens);
        Assert.Equal(ModelLimits.DefaultMaxOutputTokens, limits.MaxOutputTokens);
    }

    // An absurd configured value would either truncate every prompt to
    // nothing or blow the provider's own limit, so both are clamped.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(512)]
    public void Limits_AnAbsurdContextWindowFallsBackToTheDefault(int configured)
    {
        var limits = new ModelLimits { ContextWindowTokens = configured }.Normalize();

        Assert.Equal(ModelLimits.DefaultContextWindowTokens, limits.ContextWindowTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    public void Limits_AnAbsurdOutputBudgetFallsBackToTheDefault(int configured)
    {
        var limits = new ModelLimits { MaxOutputTokens = configured }.Normalize();

        Assert.Equal(ModelLimits.DefaultMaxOutputTokens, limits.MaxOutputTokens);
    }

    // Reserving more output than the whole window leaves no room for the
    // prompt — the contradiction is resolved in the prompt's favour.
    [Fact]
    public void Limits_OutputCanNeverConsumeTheWholeWindow()
    {
        var limits = new ModelLimits
        {
            ContextWindowTokens = 4096,
            MaxOutputTokens = 8192,
        }.Normalize();

        Assert.True(limits.MaxOutputTokens < limits.ContextWindowTokens);
        Assert.Equal(1024, limits.MaxOutputTokens);
    }

    [Fact]
    public void Limits_AGenerousConfigurationIsLeftAlone()
    {
        var limits = new ModelLimits
        {
            ContextWindowTokens = 200_000,
            MaxOutputTokens = 8_192,
        }.Normalize();

        Assert.Equal(200_000, limits.ContextWindowTokens);
        Assert.Equal(8_192, limits.MaxOutputTokens);
    }

    // The input budget is what the caller truncates live context against, so
    // it must always leave usable room.
    [Fact]
    public void Limits_TheInputBudgetIsWhatIsLeftAfterReservingOutput()
    {
        var budget = new ModelLimits
        {
            ContextWindowTokens = 100_000,
            MaxOutputTokens = 4_000,
        }.InputTokenBudget();

        Assert.Equal(96_000, budget);
    }

    [Fact]
    public void Limits_TheInputBudgetIsNeverDegenerate()
    {
        var budget = new ModelLimits
        {
            ContextWindowTokens = 2048,
            MaxOutputTokens = 2047,
        }.InputTokenBudget();

        Assert.True(budget >= 512);
    }

    // The char budget is the ~3 chars/token estimate the truncation actually
    // uses.
    [Fact]
    public void Limits_TheCharBudgetIsThreeCharsPerToken()
    {
        var limits = new ModelLimits { ContextWindowTokens = 100_000, MaxOutputTokens = 4_000 };

        Assert.Equal(limits.InputTokenBudget() * 3, limits.InputCharBudget());
    }
}
