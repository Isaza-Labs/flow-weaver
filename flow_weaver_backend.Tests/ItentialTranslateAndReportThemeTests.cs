using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Import.Translators;
using flow_weaver_backend.Utils.Report;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// Three independent surfaces batched by cost, not by theme: the Itential
// translator's end-to-end pass, the report palette's tone resolvers, and the
// admin trace feed.
//
// The translator is the one with teeth. An Itential export is a foreign DSL we
// have to turn into a runnable v1 graph; anything it silently drops becomes a
// step the imported workflow never performs, which the user only discovers on
// a live device.
public class ItentialTranslateAndReportThemeTests
{

    // ─── ItentialTranslator ─────────────────────────────────────────────

    private static async Task<(JsonElement Workflow, IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings)>
        Translate(string document)
    {
        var result = await new ItentialTranslator().TranslateAsync(TestJson.Element(document), default);
        return (result.V1Workflow, result.Notes, result.Warnings);
    }

    private static List<string> NodeIds(JsonElement workflow)
        => workflow.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()!)
            .ToList();

    private static List<(string Source, string Target, string Type)> Edges(JsonElement workflow)
        => workflow.GetProperty("edges").EnumerateArray()
            .Select(e => (
                e.GetProperty("source").GetString()!,
                e.GetProperty("target").GetString()!,
                e.GetProperty("type").GetString()!))
            .ToList();

    // A document with no tasks can't produce a graph, so the translator
    // emits a runnable skeleton plus a warning rather than an empty blob the
    // schema validator would reject.
    [Theory]
    [InlineData("""{"name":"empty"}""")]
    [InlineData("""{"name":"empty","tasks":[]}""")]
    [InlineData("""{"name":"empty","tasks":"nope"}""")]
    public async Task Itential_ADocumentWithoutTasksStillYieldsARunnableSkeleton(string document)
    {
        var (workflow, _, warnings) = await Translate(document);

        Assert.Equal("empty", workflow.GetProperty("name").GetString());
        Assert.Equal(new[] { "__start__", "__end__" }, NodeIds(workflow));
        Assert.Equal(("__start__", "__end__", "success"), Assert.Single(Edges(workflow)));
        Assert.Contains("no tasks", Assert.Single(warnings));
    }

    [Fact]
    public async Task Itential_AnUnnamedDocumentGetsAFallbackName()
    {
        var (workflow, _, _) = await Translate("{}");

        Assert.Equal("Itential import", workflow.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Itential_TheSourceFormatIsStampedOnTheMetadata()
    {
        var (workflow, _, _) = await Translate("{}");

        Assert.Equal("itential",
            workflow.GetProperty("metadata").GetProperty("source_format").GetString());
    }

    // Itential's own start/end sentinels are rewritten to FlowWeaver's, so
    // the engine's existing start/end semantics keep working.
    [Fact]
    public async Task Itential_TheSentinelsAreRewrittenToFlowWeaversOwn()
    {
        var (workflow, _, _) = await Translate("""
            {
              "name": "wf",
              "tasks": {
                "workflow_start": {"app":"WorkFlowEngine","name":"start"},
                "collect": {"app":"@itential/adapter-ssh","name":"ssh"},
                "workflow_end": {"app":"WorkFlowEngine","name":"end"}
              },
              "transitions": {
                "workflow_start": {"collect":{"type":"standard","state":"success"}},
                "collect": {"workflow_end":{"type":"standard","state":"success"}}
              }
            }
            """);

        var ids = NodeIds(workflow);
        Assert.Contains("__start__", ids);
        Assert.Contains("__end__", ids);
        Assert.DoesNotContain("workflow_start", ids);
    }

    [Fact]
    public async Task Itential_AnSshTaskBecomesAnSshNode()
    {
        var (workflow, _, _) = await Translate("""
            {"name":"wf","tasks":{"collect":{"app":"@itential/adapter-ssh","name":"run"}}}
            """);

        var node = workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "collect");
        Assert.Equal("ssh", node.GetProperty("snippet_id").GetString());
    }

    [Fact]
    public async Task Itential_AnHttpTaskBecomesARestCallNode()
    {
        var (workflow, _, _) = await Translate("""
            {"name":"wf","tasks":{"fetch":{"app":"http","name":"get"}}}
            """);

        var node = workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "fetch");
        Assert.Equal("rest_call", node.GetProperty("snippet_id").GetString());
    }

    // WorkFlowEngine tasks run arbitrary logic with no FlowWeaver
    // equivalent, so they become python placeholders the wizard can offer to
    // AI-generate — not a useless integration_action fallback.
    [Theory]
    [InlineData("transformation")]
    [InlineData("viewData")]
    [InlineData("updateJobDescription")]
    public async Task Itential_WorkFlowEngineLogicTasksBecomePythonPlaceholders(string taskName)
    {
        var (workflow, _, _) = await Translate(
            "{\"name\":\"wf\",\"tasks\":{\"logic\":{\"app\":\"WorkFlowEngine\",\"name\":\""
            + taskName + "\"}}}");

        var node = workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "logic");
        Assert.StartsWith("python_", node.GetProperty("snippet_id").GetString());
    }

    // Transitions become edges with the mapped state.
    [Fact]
    public async Task Itential_TransitionsBecomeTypedEdges()
    {
        var (workflow, _, _) = await Translate("""
            {
              "name": "wf",
              "tasks": {
                "a": {"app":"ssh","name":"x"},
                "b": {"app":"ssh","name":"y"},
                "c": {"app":"ssh","name":"z"}
              },
              "transitions": {
                "a": {"b":{"type":"standard","state":"success"},
                      "c":{"type":"standard","state":"failure"}}
              }
            }
            """);

        var edges = Edges(workflow);
        Assert.Contains(("a", "b", "success"), edges);
        Assert.Contains(("a", "c", "failure"), edges);
    }

    [Fact]
    public async Task Itential_AnUnknownTransitionStateBecomesAlwaysSoNoEdgeIsLost()
    {
        var (workflow, _, _) = await Translate("""
            {
              "name": "wf",
              "tasks": {"a":{"app":"ssh","name":"x"},"b":{"app":"ssh","name":"y"}},
              "transitions": {"a":{"b":{"type":"standard","state":"finished"}}}
            }
            """);

        Assert.Contains(("a", "b", "always"), Edges(workflow));
    }

    // Task ids that aren't legal FlowWeaver ids are sanitised, and the
    // transitions follow the same mapping so nothing dangles.
    [Fact]
    public async Task Itential_SanitisedTaskIdsStayConsistentAcrossEdges()
    {
        var (workflow, _, _) = await Translate("""
            {
              "name": "wf",
              "tasks": {"Collect Data!":{"app":"ssh","name":"x"},"b":{"app":"ssh","name":"y"}},
              "transitions": {"Collect Data!":{"b":{"type":"standard","state":"success"}}}
            }
            """);

        var sanitised = NodeIds(workflow)
            .Single(i => i is not ("b" or "__start__" or "__end__"));
        Assert.DoesNotContain(" ", sanitised);
        Assert.Contains(Edges(workflow), e => e.Source == sanitised && e.Target == "b");
    }

    // A transition pointing at a task that isn't in the document would
    // dangle; the translator must not emit an edge to nowhere.
    [Fact]
    public async Task Itential_ATransitionToAnUnknownTaskIsNotEmitted()
    {
        var (workflow, _, _) = await Translate("""
            {
              "name": "wf",
              "tasks": {"a":{"app":"ssh","name":"x"}},
              "transitions": {"a":{"ghost":{"type":"standard","state":"success"}}}
            }
            """);

        Assert.DoesNotContain(Edges(workflow), e => e.Target == "ghost");
    }

    // Sentinels are synthesised even when the source document has none, so
    // the imported graph always has the start/end the engine expects.
    [Fact]
    public async Task Itential_ADocumentWithoutTransitionsStillEmitsItsNodesPlusSentinels()
    {
        var (workflow, _, _) = await Translate("""
            {"name":"wf","tasks":{"a":{"app":"ssh","name":"x"},"b":{"app":"ssh","name":"y"}}}
            """);

        var ids = NodeIds(workflow);
        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
        Assert.Contains("__start__", ids);
        Assert.Contains("__end__", ids);
    }

    [Fact]
    public async Task Itential_TheOutputCarriesTheV1SchemaVersion()
    {
        var (workflow, _, _) = await Translate("""
            {"name":"wf","tasks":{"a":{"app":"ssh","name":"x"}}}
            """);

        Assert.Equal("v1", workflow.GetProperty("schema_version").GetString());
    }

    // Every emitted node needs the coordinates and config slot the v1 schema
    // requires, or the import fails validation right after translation.
    [Fact]
    public async Task Itential_EveryNodeCarriesTheFieldsTheV1SchemaRequires()
    {
        var (workflow, _, _) = await Translate("""
            {"name":"wf","tasks":{"a":{"app":"ssh","name":"x"}}}
            """);

        foreach (var node in workflow.GetProperty("nodes").EnumerateArray())
        {
            Assert.True(node.TryGetProperty("id", out _));
            Assert.True(node.TryGetProperty("snippet_id", out _));
            Assert.True(node.TryGetProperty("x", out _));
            Assert.True(node.TryGetProperty("y", out _));
        }
    }

    [Fact]
    public void Itential_TheTranslatorRegistersUnderTheItentialFormat()
    {
        Assert.Equal("itential", new ItentialTranslator().FormatName);
    }

    // ─── ReportTheme ────────────────────────────────────────────────────

    [Theory]
    [InlineData("critical")]
    [InlineData("high")]
    [InlineData("medium")]
    [InlineData("low")]
    [InlineData("ok")]
    [InlineData("success")]
    [InlineData("accent")]
    [InlineData("info")]
    [InlineData("warn")]
    [InlineData("warning")]
    [InlineData("danger")]
    [InlineData("error")]
    public void Theme_EveryKnownToneResolvesToAHexTriple(string tone)
    {
        Assert.StartsWith("#", ReportTheme.ToneColor(tone));
        Assert.StartsWith("#", ReportTheme.ToneBackground(tone));
        Assert.StartsWith("#", ReportTheme.ToneBorder(tone));
    }

    [Fact]
    public void Theme_SeverityTonesAreVisuallyDistinct()
    {
        var colors = new[] { "critical", "high", "medium", "low", "ok" }
            .Select(ReportTheme.ToneColor)
            .ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Fact]
    public void Theme_ToneAliasesResolveIdentically()
    {
        Assert.Equal(ReportTheme.ToneColor("ok"), ReportTheme.ToneColor("success"));
        Assert.Equal(ReportTheme.ToneColor("warn"), ReportTheme.ToneColor("warning"));
        Assert.Equal(ReportTheme.ToneColor("danger"), ReportTheme.ToneColor("error"));
    }

    // An agent-fabricated tone must render as neutral rather than breaking
    // the report.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chartreuse")]
    [InlineData("neutral")]
    public void Theme_AnUnknownToneFallsBackToNeutral(string? tone)
    {
        Assert.Equal(ReportTheme.Text, ReportTheme.ToneColor(tone));
        Assert.Equal(ReportTheme.SurfaceAlt, ReportTheme.ToneBackground(tone));
        Assert.Equal(ReportTheme.Border, ReportTheme.ToneBorder(tone));
    }

    [Fact]
    public void Theme_ToneMatchingIsCaseInsensitive()
    {
        Assert.Equal(ReportTheme.ToneColor("critical"), ReportTheme.ToneColor("CRITICAL"));
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("api")]
    [InlineData("ai")]
    [InlineData("engine")]
    [InlineData("store")]
    [InlineData("worker")]
    [InlineData("scheduler")]
    [InlineData("frontend")]
    public void Theme_EveryKnownCategoryResolvesToAHexTriple(string category)
    {
        var (fg, bg, border) = ReportTheme.Category(category);

        Assert.StartsWith("#", fg);
        Assert.StartsWith("#", bg);
        Assert.StartsWith("#", border);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unheard-of")]
    public void Theme_AnUnknownCategoryFallsBackToTheAccent(string? category)
    {
        Assert.Equal((ReportTheme.Accent, ReportTheme.AccentLight, ReportTheme.BorderFocus),
            ReportTheme.Category(category));
    }

    // ─── TraceEventsController ──────────────────────────────────────────

    private sealed class TraceFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public TraceEventsController Build()
            => new(Db, new FakeUser ());

        public Guid Seed(
            string category = "api", string action = "workflow.run", string status = "completed",
            Guid? userId = null, string? requestId = "req-1",
            DateTime? at = null, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.TraceEvents.Add(new TraceEvent
            {
                TraceEventId = id,
                Category = category,
                Action = action,
                Status = status,
                UserId = userId,
                RequestId = requestId,
                Metadata = TestJson.Element("""{"k":1}"""),
                At = at ?? DateTime.UtcNow,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static List<TraceEventResponse> Rows(ActionResult<IEnumerable<TraceEventResponse>> result)
        => Assert.IsAssignableFrom<IEnumerable<TraceEventResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value).ToList();

    private static Task<ActionResult<IEnumerable<TraceEventResponse>>> List(
        TraceFixture f, string? category = null, string? action = null, string? status = null,
        Guid? userId = null, string? requestId = null,
        DateTime? from = null, DateTime? to = null, int limit = 100, int offset = 0)
        => f.Build().List(category, action, status, userId, requestId, from, to, limit, offset);

    [Fact]
    public async Task Traces_ReturnsEventsNewestFirst()
    {
        using var f = new TraceFixture();
        var older = f.Seed(action: "older", at: DateTime.UtcNow.AddHours(-1));
        var newer = f.Seed(action: "newer", at: DateTime.UtcNow);

        var rows = Rows(await List(f));

        Assert.Equal(new[] { newer, older }, rows.Select(r => r.TraceEventId));
    }

    [Fact]
    public async Task Traces_SoftDeletedRowsAreExcluded()
    {
        using var f = new TraceFixture();
        f.Seed(active: false);

        Assert.Empty(Rows(await List(f)));
    }

    [Fact]
    public async Task Traces_FiltersByCategoryActionAndStatus()
    {
        using var f = new TraceFixture();
        f.Seed(category: "api", action: "workflow.run", status: "completed");
        f.Seed(category: "worker", action: "step.exec", status: "failed");

        Assert.Single(Rows(await List(f, category: "worker")));
        Assert.Single(Rows(await List(f, action: "workflow.run")));
        Assert.Single(Rows(await List(f, status: "failed")));
    }

    // Filtering by request_id reconstructs a single HTTP call end-to-end.
    [Fact]
    public async Task Traces_FiltersByRequestId()
    {
        using var f = new TraceFixture();
        f.Seed(requestId: "req-a");
        f.Seed(requestId: "req-b");

        Assert.Equal("req-a", Assert.Single(Rows(await List(f, requestId: "req-a"))).RequestId);
    }

    [Fact]
    public async Task Traces_FiltersByUser()
    {
        using var f = new TraceFixture();
        var user = Guid.NewGuid();
        f.Seed(userId: user);
        f.Seed(userId: Guid.NewGuid());

        Assert.Equal(user, Assert.Single(Rows(await List(f, userId: user))).UserId);
    }

    [Fact]
    public async Task Traces_FiltersByTimeWindow()
    {
        using var f = new TraceFixture();
        f.Seed(action: "old", at: DateTime.UtcNow.AddDays(-2));
        f.Seed(action: "recent", at: DateTime.UtcNow);

        Assert.Equal("recent",
            Assert.Single(Rows(await List(f, from: DateTime.UtcNow.AddDays(-1)))).Action);
        Assert.Equal("old",
            Assert.Single(Rows(await List(f, to: DateTime.UtcNow.AddDays(-1)))).Action);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9999, 3)]
    public async Task Traces_TheLimitIsClamped(int limit, int expected)
    {
        using var f = new TraceFixture();
        f.Seed();
        f.Seed();
        f.Seed();

        Assert.Equal(expected, Rows(await List(f, limit: limit)).Count);
    }

    [Fact]
    public async Task Traces_ANegativeOffsetIsTreatedAsZero()
    {
        using var f = new TraceFixture();
        f.Seed();

        Assert.Single(Rows(await List(f, offset: -3)));
    }

    [Fact]
    public async Task Traces_GetByIdReturnsTheRowWithItsMetadata()
    {
        using var f = new TraceFixture();
        var id = f.Seed();

        var result = await f.Build().Get(id, default);

        var row = Assert.IsType<TraceEventResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(id, row.TraceEventId);
        Assert.Equal(1, row.Metadata.GetProperty("k").GetInt32());
    }

    [Fact]
    public async Task Traces_GetByIdOnAnUnknownIdIs404()
    {
        using var f = new TraceFixture();

        Assert.IsType<NotFoundResult>((await f.Build().Get(Guid.NewGuid(), default)).Result);
    }

    [Fact]
    public async Task Traces_TheSummaryGroupsByCategoryAndStatus()
    {
        using var f = new TraceFixture();
        f.Seed(category: "api", status: "completed");
        f.Seed(category: "api", status: "failed");
        f.Seed(category: "worker", status: "completed");

        var body = Assert.IsType<OkObjectResult>((await f.Build().Summary()).Result).Value!;
        var json = JsonSerializer.SerializeToElement(body);

        Assert.Equal(24, json.GetProperty("window_hours").GetInt32());
        Assert.Equal(2, json.GetProperty("by_category").GetArrayLength());
        Assert.Equal(2, json.GetProperty("by_status").GetArrayLength());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99999, 720)]
    public async Task Traces_TheSummaryWindowIsClamped(int hours, int expected)
    {
        using var f = new TraceFixture();

        var body = Assert.IsType<OkObjectResult>((await f.Build().Summary(hours)).Result).Value!;

        Assert.Equal(expected,
            JsonSerializer.SerializeToElement(body).GetProperty("window_hours").GetInt32());
    }

    [Fact]
    public async Task Traces_TheSummaryExcludesRowsOutsideTheWindow()
    {
        using var f = new TraceFixture();
        f.Seed(at: DateTime.UtcNow.AddDays(-5));

        var body = Assert.IsType<OkObjectResult>((await f.Build().Summary(hours: 1)).Result).Value!;

        Assert.Equal(0,
            JsonSerializer.SerializeToElement(body).GetProperty("by_category").GetArrayLength());
    }
}
