using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Snippet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The snippet catalogue's remaining write paths: the Mermaid requirement, the
// scaffold detector and idempotency normalisation.
//
// Both validators exist because of real incidents. A python snippet with no
// logic diagram is unreviewable, and a scaffold body ("TODO: implement") that
// reaches production is a step that silently does nothing on a live device.
public class SnippetServiceWriteTests
{
    private const string Diagram = "graph TD\n  A-->B";

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeUser Caller { get; set; }
            = new() { Roles = new[] { "admin" } };

        public SnippetService Build() => new(
            new SnippetRepository(Db),
            new StepRunRepository(Db),
            Caller,
            new FakeAudit(),
            new FakeTrace(),
            NullLogger<SnippetService>.Instance);

        public Guid Seed(
            string type = "ssh", string? code = null, string? diagram = null,
            string? idempotency = null, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id,
                Name = "collect",
                Type = type,
                Code = code,
                TargetMode = "once",
                LogicDiagramMermaid = diagram,
                Idempotency = idempotency,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string ErrorOf<T>(ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    private static CreateSnippet New(
        string type = "ssh", string? code = null, string? diagram = null, string? idempotency = null)
        => new()
        {
            Name = "collect",
            Type = type,
            TargetMode = "once",
            Code = code,
            LogicDiagramMermaid = diagram,
            Idempotency = idempotency,
        };

    // ─── The Mermaid requirement ────────────────────────────────────────

    // Code-bearing types must ship a diagram: the reviewer has no other way
    // to see the intended branching before approving.
    [Theory]
    [InlineData("python_snippet")]
    [InlineData("python")]
    [InlineData("transform")]
    [InlineData("jmespath")]
    public async Task Create_CodeBearingTypesRequireALogicDiagram(string type)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New(type, code: "x = 1"));

        Assert.Contains("logic_diagram_mermaid is required", ErrorOf(result));
        Assert.Empty(f.Db.Snippets);
    }

    // Types with no author-supplied logic don't need one.
    [Theory]
    [InlineData("ssh")]
    [InlineData("rest_call")]
    [InlineData("ping")]
    [InlineData("integration_action")]
    public async Task Create_NonCodeTypesNeedNoDiagram(string type)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New(type));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // A diagram that isn't Mermaid at all would render as a broken box in
    // the UI, so the first non-blank line has to be a real directive.
    [Theory]
    [InlineData("just some prose")]
    [InlineData("A --> B")]
    [InlineData("   ")]
    public async Task Create_ADiagramThatIsNotMermaidIsRejected(string diagram)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(
            New("python_snippet", code: "x = 1", diagram: diagram));

        Assert.NotEmpty(ErrorOf(result));
    }

    [Theory]
    [InlineData("graph TD\n  A-->B")]
    [InlineData("flowchart LR\n  A-->B")]
    [InlineData("sequenceDiagram\n  A->>B: hi")]
    public async Task Create_EveryMermaidDirectiveIsAccepted(string diagram)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(
            New("python_snippet", code: "x = 1", diagram: diagram));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // Leading Mermaid comments are legal and must not be mistaken for the
    // directive line.
    [Fact]
    public async Task Create_LeadingMermaidCommentsAreSkipped()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New(
            "python_snippet", code: "x = 1",
            diagram: "%% collects the LLDP table\n\ngraph TD\n  A-->B"));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ─── The scaffold detector ──────────────────────────────────────────

    // A placeholder body that reaches production is a step that silently
    // does nothing on a live device.
    [Theory]
    [InlineData("# TODO: implement")]
    [InlineData("# placeholder snippet")]
    [InlineData("# not yet implemented")]
    public async Task Create_AScaffoldBodyIsRejected(string code)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New("python_snippet", code: code, diagram: Diagram));

        Assert.Contains("scaffold", ErrorOf(result));
        Assert.Empty(f.Db.Snippets);
    }

    // The second gate: a script that genuinely does work is allowed even if
    // it mentions "placeholder" in a log line or comment.
    [Theory]
    [InlineData("# TODO: tidy this up\nresult = integration('mail', 'send')")]
    [InlineData("# placeholder snippet\nimport subprocess\nsubprocess.run(['ls'])")]
    [InlineData("# TODO\nimport socket\nsocket.gethostname()")]
    public async Task Create_ARealScriptSurvivesTheScaffoldMarker(string code)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New("python_snippet", code: code, diagram: Diagram));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // The detector only applies to types that carry a script at all.
    [Fact]
    public async Task Create_TheScaffoldDetectorIgnoresNonScriptTypes()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New("ssh", code: "# TODO: implement"));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Create_AnEmptyBodyIsNotAScaffold()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(New("python_snippet", code: null, diagram: Diagram));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ─── Idempotency normalisation ──────────────────────────────────────

    // The rollback analyzer compares this against its own constants, so a
    // hand-typed value has to be folded to lowercase or the override is
    // silently ignored.
    [Theory]
    [InlineData("NonReversible", "nonreversible")]
    [InlineData("  Idempotent  ", "idempotent")]
    public async Task Create_TheIdempotencyOverrideIsNormalised(string raw, string expected)
    {
        using var f = new Fixture();

        await f.Build().PostAsync(New(idempotency: raw));

        Assert.Equal(expected, f.Db.Snippets.Single().Idempotency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_AnAbsentOverrideStaysNull(string? raw)
    {
        using var f = new Fixture();

        await f.Build().PostAsync(New(idempotency: raw));

        Assert.Null(f.Db.Snippets.Single().Idempotency);
    }

    // Omitting the field on update keeps the current override — the DTO has
    // no way to distinguish "omitted" from "null".
    [Fact]
    public async Task Update_AnOmittedOverrideIsKept()
    {
        using var f = new Fixture();
        var id = f.Seed(idempotency: "idempotent");

        await f.Build().UpdateAsync(id, new UpdateSnippet { Description = "touched" });

        Assert.Equal("idempotent", f.Db.Snippets.Single().Idempotency);
    }

    [Fact]
    public async Task Update_ASuppliedOverrideIsNormalised()
    {
        using var f = new Fixture();
        var id = f.Seed(idempotency: "idempotent");

        await f.Build().UpdateAsync(id, new UpdateSnippet { Idempotency = "NonReversible" });

        Assert.Equal("nonreversible", f.Db.Snippets.Single().Idempotency);
    }

    // ─── Update re-validation ───────────────────────────────────────────

    // Swapping only the diagram on a code-bearing snippet is re-validated —
    // otherwise an edit could strip the diagram the create demanded.
    [Fact]
    public async Task Update_RemovingTheDiagramFromACodeSnippetIsRejected()
    {
        using var f = new Fixture();
        var id = f.Seed("python_snippet", code: "x = 1", diagram: Diagram);

        var result = await f.Build().UpdateAsync(
            id, new UpdateSnippet { LogicDiagramMermaid = "not mermaid" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(Diagram, f.Db.Snippets.Single().LogicDiagramMermaid);
    }

    // Changing the TYPE re-triggers the diagram requirement: an ssh snippet
    // turned into python needs one it never had.
    [Fact]
    public async Task Update_PromotingASnippetToACodeTypeDemandsADiagram()
    {
        using var f = new Fixture();
        var id = f.Seed("ssh");

        var result = await f.Build().UpdateAsync(id, new UpdateSnippet { Type = "python_snippet" });

        Assert.Contains("logic_diagram_mermaid is required", ErrorOf(result));
    }

    // ─── Delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_IsSoftAndHidesTheRow()
    {
        using var f = new Fixture();
        var id = f.Seed();

        await f.Build().DeleteAsync(id);

        Assert.False(f.Db.Snippets.Single().IsActive);
        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(id)).Result);
    }

    [Fact]
    public async Task Delete_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().DeleteAsync(Guid.NewGuid())).Result);
    }

}

