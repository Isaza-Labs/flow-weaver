using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// Traceability: B-02 (TC-FW-B02) — RBAC on the API surface. When per-resource
// granular gating is ON and the caller holds no grant, mutating a workflow is
// denied. WorkflowService.AuthorizeAsync consults IResourcePermissionService
// and returns 403 (missing_editor_grant for edit, missing_owner_grant for
// delete). Exercised at the service layer with a deny-all permission service.
//
// The full API+audit chain (auth middleware → audit row) remains an
// integration-level concern; this pins the authorization decision itself.
public class WorkflowRbacGateTests
{
    private static readonly FakeUser Caller = new();

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static Guid SeedDraft(AppDbContext db)
    {
        var wf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = "wf",
            Version = 1,
            Environment = "draft",
            Nodes = JsonDocument.Parse("[]").RootElement,
            Edges = JsonDocument.Parse("[]").RootElement,
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf.WorkflowId;
    }

    private static WorkflowService NewService(AppDbContext db) =>
        new(
            new RepositoryBase<WorkflowModel>(db),
            Caller,
            schemaValidator: null!,
            referenceValidator: null!,
            vendorCommandValidator: null!,
            audit: new NoOpAuditLogger(),
            trace: new NoOpTraceLogger(),
            policies: null!,
            permissions: new DenyAllPermissions(),
            appSettings: new GatingEnabledAppSettings(),
            logger: NullLogger<WorkflowService>.Instance);

    [Fact]
    public async Task Update_is_denied_403_when_gating_on_and_no_editor_grant()
    {
        using var db = NewDb(nameof(Update_is_denied_403_when_gating_on_and_no_editor_grant));
        var id = SeedDraft(db);

        var result = await NewService(db).UpdateAsync(id, new UpdateWorkflow { Name = "x" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, obj.StatusCode);
    }

    [Fact]
    public async Task Delete_is_denied_403_when_gating_on_and_no_owner_grant()
    {
        using var db = NewDb(nameof(Delete_is_denied_403_when_gating_on_and_no_owner_grant));
        var id = SeedDraft(db);

        var result = await NewService(db).DeleteAsync(id);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, obj.StatusCode);
    }
}

// Granular gating ON → AuthorizeAsync consults the permission service.
internal sealed class GatingEnabledAppSettings : IAppSettingsService
{
    public Task<AppSettings> GetAsync(CancellationToken ct = default)
        => Task.FromResult(new AppSettings { PermissionsGranularGatingEnabled = true });

    public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
        => Task.FromResult(updated);
}

// Denies every per-resource check. The grant/list/revoke members are never
// reached on the authorization path under test.
internal sealed class DenyAllPermissions : IResourcePermissionService
{
    public Task<bool> HasAtLeastAsync(string resourceType, Guid resourceId, string requiredRole, CancellationToken ct)
        => Task.FromResult(false);

    public Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(string resourceType, Guid resourceId, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<ResourcePermissionResponse> GrantAsync(string resourceType, Guid resourceId, GrantResourcePermissionRequest dto, CancellationToken ct)
        => throw new NotSupportedException();

    public Task RevokeAsync(Guid permissionId, CancellationToken ct)
        => throw new NotSupportedException();
}
