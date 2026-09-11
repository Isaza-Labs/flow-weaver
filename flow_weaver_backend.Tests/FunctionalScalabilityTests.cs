using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Compiler;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Validation;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using McpServerModel = flow_weaver_backend.Models.McpServer;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// Traceability: NFR-004 / TC-FW-030 — "functional scalability for new tasks and
// integrations: adding capability does NOT degrade existing workflows; the
// catalog supports growth". Per Requirement.md §5, NFR-004 is satisfied by the
// DI plug-in model for `ISnippetHandler` (task types) and `IWorkflowExporter`
// (export formats) — i.e. FUNCTIONAL extensibility, not a raw load test. This
// suite pins the "additive, isolated, non-degrading, gracefully-degrading"
// invariants of that plug-in model (InMemory EF + fakes; no Postgres):
//
//   A. IWorkflowExporter plug-in — a newly-registered exporter is resolved and
//      used, does NOT change the built-in yaml/json formats or shadow other
//      exporters, and an unknown format degrades to a ValidationException whose
//      supported-list reflects the registered plug-ins (catalog growth surfaced).
//   B. New task-type openness — a workflow referencing a snippet of a BRAND-NEW
//      type passes reference validation unchanged (only integration_action /
//      mcp_call carry a type-specific gate), and the promotion rollback analyzer
//      treats an unknown/unregistered handler type CONSERVATIVELY
//      (RequiresCompensation) so a new type can never silently ship as reversible.
//
// OUT OF SCOPE (documented, not covered): the quantitative "catalog grows to N
// entries within SLA" / load-and-capacity dimension (hundreds/thousands of
// synthetic devices, throughput thresholds) — that is dedicated performance
// engineering on a separate staging environment with SLA thresholds defined by
// the product owner, per the TC-FW-030 blocker note.
public class FunctionalScalabilityTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private WorkflowModel SeedWorkflow(AppDbContext db)
    {
        var wf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = "wf",
            Environment = "draft",
            SchemaVersion = "v1",
            Nodes = JsonDocument.Parse("[]").RootElement.Clone(),
            Edges = JsonDocument.Parse("[]").RootElement.Clone(),
            InputSchema = JsonDocument.Parse("{}").RootElement.Clone(),
            Metadata = JsonDocument.Parse("{}").RootElement.Clone(),
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf;
    }

    private WorkflowExportService NewExportService(AppDbContext db, params IWorkflowExporter[] exporters)
        => new(new RepositoryBase<WorkflowModel>(db), new SnippetRepository(db),
               new WorkflowYamlCompiler(NullLogger<WorkflowYamlCompiler>.Instance),
               exporters);

    // ── A. IWorkflowExporter plug-in extensibility ────────────────────────────

    [Fact]
    public async Task A_newly_registered_exporter_is_resolved_and_used()
    {
        using var db = NewDb(nameof(A_newly_registered_exporter_is_resolved_and_used));
        var wf = SeedWorkflow(db);
        var svc = NewExportService(db, new FakeExporter("customfmt", "FAKE-BODY"));

        var payload = await svc.ExportAsync(wf.WorkflowId, "customfmt", CancellationToken.None);

        Assert.Equal("FAKE-BODY", payload.Content);
        Assert.Equal("text/x-fake", payload.ContentType);
        Assert.EndsWith(".customfmt", payload.Filename);
    }

    [Fact]
    public async Task Adding_an_exporter_does_not_change_the_builtin_formats()
    {
        // The built-in json case must NOT be routed to (or shadowed by) a
        // plug-in — adding capability leaves existing formats intact.
        using var db = NewDb(nameof(Adding_an_exporter_does_not_change_the_builtin_formats));
        var wf = SeedWorkflow(db);
        var svc = NewExportService(db, new FakeExporter("customfmt", "FAKE-BODY"));

        var payload = await svc.ExportAsync(wf.WorkflowId, "json", CancellationToken.None);

        Assert.Equal("application/json", payload.ContentType);
        Assert.DoesNotContain("FAKE-BODY", payload.Content);
    }

    [Fact]
    public async Task Multiple_exporters_resolve_independently_by_format()
    {
        // Isolation: two plug-ins coexist; each format resolves to its own
        // exporter, so adding one never hijacks another.
        using var db = NewDb(nameof(Multiple_exporters_resolve_independently_by_format));
        var wf = SeedWorkflow(db);
        var svc = NewExportService(db,
            new FakeExporter("fmt1", "ONE"),
            new FakeExporter("fmt2", "TWO"));

        Assert.Equal("ONE", (await svc.ExportAsync(wf.WorkflowId, "fmt1", CancellationToken.None)).Content);
        Assert.Equal("TWO", (await svc.ExportAsync(wf.WorkflowId, "fmt2", CancellationToken.None)).Content);
    }

    [Fact]
    public async Task Unknown_format_degrades_gracefully_and_lists_registered_plugins()
    {
        // Graceful degradation: an unsupported format is a domain
        // ValidationException (not a crash), and the supported-list is built
        // from the registered exporters — so the catalog's growth is surfaced.
        using var db = NewDb(nameof(Unknown_format_degrades_gracefully_and_lists_registered_plugins));
        var wf = SeedWorkflow(db);
        var svc = NewExportService(db, new FakeExporter("customfmt", "x"));

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ExportAsync(wf.WorkflowId, "totally-unknown", CancellationToken.None));

        Assert.Contains("customfmt", ex.Message);   // the plug-in is advertised
        Assert.Contains("yaml", ex.Message);         // built-ins still listed
    }

    // ── B. New task-type openness ─────────────────────────────────────────────

    [Fact]
    public async Task A_brand_new_snippet_type_passes_reference_validation_unchanged()
    {
        // The reference validator gates only integration_action / mcp_call; a
        // node referencing a snippet of a NOVEL type validates with no edits to
        // any validation path — the core "adding a task type is additive" claim.
        using var db = NewDb(nameof(A_brand_new_snippet_type_passes_reference_validation_unchanged));
        var snippetId = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = snippetId,
            Name = "quantum",
            Type = "quantum_task",   // a task type no handler/validator knows yet
        });
        await db.SaveChangesAsync();

        var nodes = JsonDocument.Parse(
            "[{\"id\":\"n1\",\"type\":\"task\",\"snippet_id\":\"" + snippetId + "\"}]").RootElement;

        var result = await NewReferenceValidator(db).ValidateAsync(nodes, CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task An_unknown_handler_type_defaults_to_requires_compensation()
    {
        // Forward-compat safety: the rollback analyzer maps a snippet whose Type
        // has NO registered handler to RequiresCompensation, so an unrecognised
        // (e.g. newer) type can never be treated as freely reversible — it is
        // flagged for a compensation edge before it can be promoted.
        using var db = NewDb(nameof(An_unknown_handler_type_defaults_to_requires_compensation));
        var snippetId = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = snippetId,
            Name = "quantum",
            Type = "quantum_task",
        });
        await db.SaveChangesAsync();

        var wf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = "wf",
            Nodes = JsonDocument.Parse(
                "[{\"id\":\"n1\",\"snippet_id\":\"" + snippetId + "\"}]").RootElement.Clone(),
            Edges = JsonDocument.Parse("[]").RootElement.Clone(),
        };

        // No handlers registered → every type is unknown → conservative default.
        var analyzer = new WorkflowRollbackAnalyzer(
            new SnippetRepository(db), Array.Empty<ISnippetHandler>());

        var report = await analyzer.AnalyzeAsync(wf, CancellationToken.None);

        Assert.Single(report.RequiresCompensation);
        Assert.Empty(report.NonReversible);
        Assert.Empty(report.CompensatingFailureEdges);
    }

    // The reference validator gates only integration_action / mcp_call, so a
    // novel-type node never reaches the permission check — _effective is not on
    // this path and stays null (same pattern as the other guard-path tests).
    private WorkflowReferenceValidator NewReferenceValidator(AppDbContext db)
        => new(
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new RepositoryBase<IntegrationActionModel>(db),
            new RepositoryBase<McpServerModel>(db),
            effective: null!,
            appSettings: new FakeAppSettings(),
            logger: NullLogger<WorkflowReferenceValidator>.Instance);

    // Minimal exporter plug-in: a distinct Format + a fixed body.
    private sealed class FakeExporter : IWorkflowExporter
    {
        private readonly string _content;
        public FakeExporter(string format, string content) { Format = format; _content = content; }

        public string Format { get; }
        public string ContentType => "text/x-fake";
        public string FileExtension => Format;

        public Task<string> ExportAsync(
            WorkflowModel workflow, IReadOnlyDictionary<Guid, SnippetModel> snippets, CancellationToken ct)
            => Task.FromResult(_content);
    }
}
