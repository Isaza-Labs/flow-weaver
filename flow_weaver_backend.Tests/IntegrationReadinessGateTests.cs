using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Tests;

// Traceability: FR-026 (TC-FW-026) — a run that references an integration
// still in needs_config must be rejected at enqueue time, BEFORE any StepRun
// or job row is written. WorkflowExecutor.ValidateIntegrationsReadyAsync is the
// gate EnqueueRunAsync runs before it creates the run; exercising it directly
// pins the exact invariant without the full scope/queue machinery.
public class IntegrationReadinessGateTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    // A single integration_action node whose config_overrides reference the
    // given integration id — the shape ValidateIntegrationsReadyAsync scans.
    // Built by concatenation, not interpolation: the nested config_overrides
    // object ends in `}}`, which collides with `$$"""..."""` interpolation.
    private static JsonElement Nodes(Guid integrationId) => JsonDocument.Parse(
        "[{\"id\":\"a\",\"snippet_id\":\"integration_action\",\"config_overrides\":{\"integration_id\":\""
        + integrationId + "\"}}]").RootElement;

    private static void SeedIntegration(AppDbContext db, Guid id, string status) =>
        db.Integrations.Add(new Integration
        {
            IntegrationId = id,
            Name = "netbox",
            Status = status,
        });

    [Fact]
    public async Task Rejects_run_when_referenced_integration_is_needs_config()
    {
        using var db = NewDb(nameof(Rejects_run_when_referenced_integration_is_needs_config));
        var integrationId = Guid.NewGuid();
        SeedIntegration(db, integrationId, IntegrationStatus.NeedsConfig);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            WorkflowExecutor.ValidateIntegrationsReadyAsync(
                db, Nodes(integrationId), default));
        Assert.Contains("needs_config", ex.Message);
        Assert.Contains("netbox", ex.Message);
    }

    [Fact]
    public async Task Allows_run_when_referenced_integration_is_healthy()
    {
        using var db = NewDb(nameof(Allows_run_when_referenced_integration_is_healthy));
        var integrationId = Guid.NewGuid();
        SeedIntegration(db, integrationId, IntegrationStatus.Healthy);
        await db.SaveChangesAsync();

        // Must not throw — a ready integration clears the gate.
        await WorkflowExecutor.ValidateIntegrationsReadyAsync(
            db, Nodes(integrationId), default);
    }

    [Fact]
    public async Task No_integration_references_is_a_noop()
    {
        using var db = NewDb(nameof(No_integration_references_is_a_noop));
        var nodes = JsonDocument.Parse("""[{"id":"a","snippet_id":"__start__"}]""").RootElement;

        await WorkflowExecutor.ValidateIntegrationsReadyAsync(
            db, nodes, default);
    }
}
