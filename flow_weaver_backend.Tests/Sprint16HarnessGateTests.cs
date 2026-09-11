using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// S16 — regression suite for the "no victory until verified" gate.
//
// Each [Fact] mirrors one of the scenarios A–L from the logic review of
// the Sprint 16 design. The wiring is intentionally heavy: PromotionService
// has real dependencies on policy, audit, trace, and the rollback
// analyzer, so we stand up the entire object graph (with no-op stubs for
// the parts we don't care about) and exercise the public surface.
//
// Scenarios F (concurrency lost-update) is omitted — see the comment in
// SimulateWorkflowRunHandler.ExecuteAsync for the documented follow-up
// that needs RowVersion across the model.
public class Sprint16HarnessGateTests
{
    private readonly FakeUser _caller = new();

    private static AppDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private PromotionService NewPromotionService(AppDbContext db) =>
        new(
            new WorkflowRepository(db),
            new WorkflowVersionRepository(db),
            new RepositoryBase<SimulationResult>(db),
            new UnitOfWork(db),
            _caller,
            new NoOpAuditLogger(),
            new NoOpTraceLogger(),
            new AlwaysAllowPolicyEvaluator(),
            new WorkflowRollbackAnalyzer(new SnippetRepository(db), Array.Empty<ISnippetHandler>()),
            new flow_weaver_backend.Services.Permission.EffectivePermissions(_caller, new PermissionGrantReader(db)),
            new GatingEnabledAppSettings(),
            NullLogger<PromotionService>.Instance);

    private SimulateWorkflowRunHandler NewSimulator(AppDbContext db) =>
        new(
            new RepositoryBase<Workflow>(db),
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new IntegrationActionRepository(db),
            new SimulationResultRepository(db),
            _caller,
            NullLogger<SimulateWorkflowRunHandler>.Instance);

    private MarkWorkflowReadyHandler NewMarker(AppDbContext db) =>
        new(
            new RepositoryBase<Workflow>(db),
            new SimulationResultRepository(db),
            _caller,
            NullLogger<MarkWorkflowReadyHandler>.Instance);

    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<Workflow> SeedDraftAsync(AppDbContext db, string nodesJson, string edgesJson, string? name = null)
    {
        var wf = new Workflow
        {
            WorkflowId = Guid.NewGuid(),
            Name = name ?? "wf-" + Guid.NewGuid().ToString("n")[..6],
            Version = 1,
            Environment = "draft",
            Nodes = E(nodesJson),
            Edges = E(edgesJson),
            Metadata = E("{}"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();
        return wf;
    }

    private static string TrivialNodes() =>
        """[{"id":"__start__","snippet_id":"__start__"},{"id":"__end__","snippet_id":"__end__"}]""";

    private static string TrivialEdges() =>
        """[{"source":"__start__","target":"__end__","type":"success"}]""";

    private static PromoteRequest PromoteToQa() =>
        new() { TargetEnvironment = "qa", PromotedBy = "tester", ChangeSummary = "promote" };

    // ─── A — Happy path ─────────────────────────────────────────────

    [Fact]
    public async Task A_simulate_then_promote_to_qa_succeeds()
    {
        using var db = NewContext(nameof(A_simulate_then_promote_to_qa_succeeds));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());

        var simResult = await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);
        Assert.True(simResult.GetProperty("ok").GetBoolean());

        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);
        Assert.IsType<ObjectResult>(promote.Result);
        var obj = (ObjectResult)promote.Result!;
        Assert.Equal(201, obj.StatusCode);
    }

    // ─── B — No simulation at all ───────────────────────────────────

    [Fact]
    public async Task B_promote_without_simulation_is_blocked_with_412()
    {
        using var db = NewContext(nameof(B_promote_without_simulation_is_blocked_with_412));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());

        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);

        var obj = Assert.IsType<ObjectResult>(promote.Result);
        Assert.Equal(412, obj.StatusCode);
        Assert.Contains("simulate_workflow_run", ToErrorString(obj.Value));
    }

    // ─── C — Simulation reported issues ─────────────────────────────

    [Fact]
    public async Task C_promote_when_simulation_failed_is_blocked()
    {
        using var db = NewContext(nameof(C_promote_when_simulation_failed_is_blocked));
        // Node references a snippet UUID that doesn't exist → simulator
        // returns ok=false but still stamps LastSimulationId.
        var orphan = Guid.NewGuid();
        var nodes = $$"""[{"id":"a","snippet_id":"{{orphan}}"}]""";
        var wf = await SeedDraftAsync(db, nodes, "[]");

        var simResult = await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);
        Assert.False(simResult.GetProperty("ok").GetBoolean());

        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);

        var obj = Assert.IsType<ObjectResult>(promote.Result);
        Assert.Equal(412, obj.StatusCode);
        Assert.Contains("structural issue", ToErrorString(obj.Value));
    }

    // ─── D — Edit after simulation invalidates the gate ─────────────

    [Fact]
    public async Task D_edit_after_simulation_blocks_promotion_via_stale_hash()
    {
        using var db = NewContext(nameof(D_edit_after_simulation_blocks_promotion_via_stale_hash));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());
        await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        // Simulate a structural edit OUTSIDE WorkflowService (the gate
        // must hold even if a caller bypasses the service). The hash
        // check is the safety net when LastSimulationId is not cleared.
        var trackedWf = await db.Workflows.FirstAsync(w => w.WorkflowId == wf.WorkflowId);
        trackedWf.Nodes = E("""[{"id":"x","snippet_id":"__start__"},{"id":"__end__","snippet_id":"__end__"}]""");
        await db.SaveChangesAsync();

        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);

        var obj = Assert.IsType<ObjectResult>(promote.Result);
        Assert.Equal(412, obj.StatusCode);
        Assert.Contains("changed since the last simulation", ToErrorString(obj.Value));
    }

    [Fact]
    public async Task D2_clearing_LastSimulationId_blocks_promotion()
    {
        using var db = NewContext(nameof(D2_clearing_LastSimulationId_blocks_promotion));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());
        await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        // Mimic WorkflowService.UpdateAsync invalidation on structural edit.
        var tracked = await db.Workflows.FirstAsync(w => w.WorkflowId == wf.WorkflowId);
        tracked.LastSimulationId = null;
        await db.SaveChangesAsync();

        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);

        var obj = Assert.IsType<ObjectResult>(promote.Result);
        Assert.Equal(412, obj.StatusCode);
        Assert.Contains("simulate_workflow_run", ToErrorString(obj.Value));
    }

    // ─── E — Cosmetic-only edits keep the gate passing ──────────────

    [Fact]
    public async Task E_cosmetic_edit_does_not_invalidate_gate()
    {
        using var db = NewContext(nameof(E_cosmetic_edit_does_not_invalidate_gate));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges(), name: "old-name");
        await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        // Name-only edit. Don't touch Nodes/Edges, don't clear
        // LastSimulationId — the same behavior WorkflowService gives a
        // rename DTO.
        var tracked = await db.Workflows.FirstAsync(w => w.WorkflowId == wf.WorkflowId);
        tracked.Name = "new-name";
        await db.SaveChangesAsync();

        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);

        var obj = Assert.IsType<ObjectResult>(promote.Result);
        Assert.Equal(201, obj.StatusCode);
    }

    // ─── G — qa→production does NOT require a fresh simulation ──────

    [Fact]
    public async Task G_qa_to_production_does_not_require_simulation()
    {
        using var db = NewContext(nameof(G_qa_to_production_does_not_require_simulation));
        // A qa row that has never been simulated. The promoter still
        // needs approved_by, but the simulation gate must not fire.
        var wf = new Workflow
        {
            WorkflowId = Guid.NewGuid(),
            Name = "qa-wf",
            Version = 1,
            Environment = "qa",
            Nodes = E(TrivialNodes()),
            Edges = E(TrivialEdges()),
            Metadata = E("{}"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            // LastSimulationId intentionally null.
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var req = new PromoteRequest
        {
            TargetEnvironment = "production",
            PromotedBy = "tester",
            ApprovedBy = "manager",
            ChangeSummary = "go live",
        };
        var promote = await NewPromotionService(db).PromoteAsync(wf.WorkflowId, req, default);

        var obj = Assert.IsType<ObjectResult>(promote.Result);
        Assert.Equal(201, obj.StatusCode);
    }

    // ─── H — Rollback drafts start without a simulation pointer ─────

    [Fact]
    public async Task H_rollback_creates_draft_with_null_LastSimulationId()
    {
        using var db = NewContext(nameof(H_rollback_creates_draft_with_null_LastSimulationId));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());
        await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);
        await NewPromotionService(db).PromoteAsync(wf.WorkflowId, PromoteToQa(), default);

        // The promotion just snapshotted Version=1; rollback to that
        // snapshot should produce a fresh draft with no simulation
        // pointer (the snapshot's graph wasn't necessarily simulated
        // against this row).
        var rollback = await NewPromotionService(db).RollbackAsync(wf.WorkflowId, toVersion: 1, default);
        var resp = ExtractResponse(rollback);

        var rolled = await db.Workflows.AsNoTracking()
            .FirstAsync(w => w.WorkflowId == resp.WorkflowId);
        Assert.Equal("draft", rolled.Environment);
        Assert.Null(rolled.LastSimulationId);
    }

    // ─── I — Clones start without a simulation pointer ──────────────

    [Fact]
    public async Task I_clone_creates_draft_with_null_LastSimulationId()
    {
        using var db = NewContext(nameof(I_clone_creates_draft_with_null_LastSimulationId));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());
        await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        var clone = await NewPromotionService(db).CloneAsync(wf.WorkflowId, default);
        var resp = ExtractResponse(clone);

        var cloned = await db.Workflows.AsNoTracking()
            .FirstAsync(w => w.WorkflowId == resp.WorkflowId);
        Assert.Equal("draft", cloned.Environment);
        Assert.Null(cloned.LastSimulationId);
    }

    // ─── J — simulate_workflow_run rejects non-draft rows ───────────

    [Fact]
    public async Task J_simulate_rejects_non_draft()
    {
        using var db = NewContext(nameof(J_simulate_rejects_non_draft));
        var wf = new Workflow
        {
            WorkflowId = Guid.NewGuid(),
            Name = "qa-wf",
            Version = 1,
            Environment = "qa",
            Nodes = E(TrivialNodes()),
            Edges = E(TrivialEdges()),
            Metadata = E("{}"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        Assert.True(result.TryGetProperty("error", out var err));
        Assert.Contains("draft", err.GetString());
    }

    // ─── K — Hash is stable across key reordering + number forms ────

    [Fact]
    public void K_schema_hash_is_canonical_across_key_order()
    {
        var a = E("""[{"id":"n","snippet_id":"__start__","type":"start"}]""");
        var b = E("""[{"type":"start","snippet_id":"__start__","id":"n"}]""");
        Assert.Equal(
            SimulateWorkflowRunHandler.ComputeSchemaHash(a, E("[]")),
            SimulateWorkflowRunHandler.ComputeSchemaHash(b, E("[]")));
    }

    [Fact]
    public void K2_schema_hash_normalizes_numeric_forms()
    {
        var a = E("""[{"id":"n","weight":1}]""");
        var b = E("""[{"id":"n","weight":1.0}]""");
        var c = E("""[{"id":"n","weight":1.00}]""");
        var ha = SimulateWorkflowRunHandler.ComputeSchemaHash(a, E("[]"));
        var hb = SimulateWorkflowRunHandler.ComputeSchemaHash(b, E("[]"));
        var hc = SimulateWorkflowRunHandler.ComputeSchemaHash(c, E("[]"));
        Assert.Equal(ha, hb);
        Assert.Equal(hb, hc);
    }

    [Fact]
    public void K3_schema_hash_differs_when_content_actually_changes()
    {
        var a = E("""[{"id":"n","snippet_id":"__start__"}]""");
        var b = E("""[{"id":"n","snippet_id":"__end__"}]""");
        Assert.NotEqual(
            SimulateWorkflowRunHandler.ComputeSchemaHash(a, E("[]")),
            SimulateWorkflowRunHandler.ComputeSchemaHash(b, E("[]")));
    }

    // ─── L — mark_workflow_ready rejects non-draft rows ─────────────

    [Fact]
    public async Task L_mark_workflow_ready_rejects_non_draft()
    {
        using var db = NewContext(nameof(L_mark_workflow_ready_rejects_non_draft));
        var wf = new Workflow
        {
            WorkflowId = Guid.NewGuid(),
            Name = "qa-wf",
            Version = 1,
            Environment = "qa",
            Nodes = E(TrivialNodes()),
            Edges = E(TrivialEdges()),
            Metadata = E("{}"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewMarker(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        Assert.False(result.GetProperty("ready").GetBoolean());
        Assert.Equal("not_a_draft", result.GetProperty("blocker").GetString());
    }

    // ─── Bonus — happy mark_workflow_ready stamps the marker ────────

    [Fact]
    public async Task Mark_ready_happy_path_stamps_metadata()
    {
        using var db = NewContext(nameof(Mark_ready_happy_path_stamps_metadata));
        var wf = await SeedDraftAsync(db, TrivialNodes(), TrivialEdges());
        await NewSimulator(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        var result = await NewMarker(db).ExecuteAsync(E($$"""{"workflow_id":"{{wf.WorkflowId}}"}"""), default);

        Assert.True(result.GetProperty("ready").GetBoolean());
        Assert.True(result.GetProperty("metadata_stamped").GetBoolean());

        var reloaded = await db.Workflows.AsNoTracking()
            .FirstAsync(w => w.WorkflowId == wf.WorkflowId);
        Assert.True(reloaded.Metadata.TryGetProperty("ready_marker", out _));
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static string ToErrorString(object? value)
    {
        // Promotion errors are `new { error = "..." }` — pluck the field
        // via JSON so we don't depend on anonymous-type runtime shape.
        if (value is null) return string.Empty;
        var json = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? string.Empty : json;
    }

    private static WorkflowResponse ExtractResponse(ActionResult<WorkflowResponse> ar)
    {
        if (ar.Value is not null) return ar.Value;
        if (ar.Result is ObjectResult or && or.Value is WorkflowResponse wr) return wr;
        throw new InvalidOperationException("expected WorkflowResponse");
    }
}
