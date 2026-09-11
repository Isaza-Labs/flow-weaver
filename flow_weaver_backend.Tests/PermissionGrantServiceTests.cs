using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Permission.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Phase 3 of the RBAC-granular refactor (plan_rbac_granular.md): the management
// API for permission grants — CRUD, capability validation against the
// catalogue, the built-in read-only guard, and subject assignment.
public class PermissionGrantServiceTests
{
    private static readonly FakeUser Caller = new();

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static PermissionGrantService Svc(AppDbContext db) =>
        new(new RepositoryBase<PermissionGrant>(db), Caller, new FakeAudit(), NullLogger<PermissionGrantService>.Instance);

    private static CreatePermissionGrant Create(params string[] caps) => new()
    {
        Name = "QA operators",
        Capabilities = caps.ToList(),
        SubjectIds = new(),
    };

    private static Guid SeedBuiltin(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.PermissionGrants.Add(new PermissionGrant
        {
            PermissionGrantId = id,
            Name = BuiltinGrantSync.OperatorGrantName,
            IsBuiltIn = true,
            Enabled = true,
            IsActive = true,
            Capabilities = new() { "workflow.read" },
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Create_persists_grant_and_normalizes_capabilities()
    {
        using var db = NewDb(nameof(Create_persists_grant_and_normalizes_capabilities));

        var result = await Svc(db).PostAsync(Create("Workflow.Run", "workflow.run", "device.read"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<PermissionGrantResponse>(created.Value);
        Assert.False(body.IsBuiltIn);
        // de-duplicated + lower-cased.
        Assert.Equal(new[] { "workflow.run", "device.read" }.OrderBy(x => x),
            body.Capabilities.OrderBy(x => x));
    }

    [Fact]
    public async Task Create_rejects_unknown_capability()
    {
        using var db = NewDb(nameof(Create_rejects_unknown_capability));

        var result = await Svc(db).PostAsync(Create("workflow.run", "workflow.teleport"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_rejects_reserved_builtin_name()
    {
        using var db = NewDb(nameof(Create_rejects_reserved_builtin_name));
        var dto = Create("workflow.run");
        dto.Name = "builtin.mine";

        var result = await Svc(db).PostAsync(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_rejects_non_object_conditions()
    {
        using var db = NewDb(nameof(Create_rejects_non_object_conditions));
        var dto = Create("workflow.run");
        dto.Conditions = JsonDocument.Parse("[]").RootElement.Clone();

        var result = await Svc(db).PostAsync(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_and_delete_reject_builtin_grants()
    {
        using var db = NewDb(nameof(Update_and_delete_reject_builtin_grants));
        var id = SeedBuiltin(db);
        var svc = Svc(db);

        var update = await svc.UpdateAsync(id, new UpdatePermissionGrant { Enabled = false });
        var del = await svc.DeleteAsync(id);

        Assert.IsType<BadRequestObjectResult>(update.Result);
        Assert.IsType<BadRequestObjectResult>(del.Result);
    }

    [Fact]
    public async Task AddSubject_rejects_builtin_grants()
    {
        using var db = NewDb(nameof(AddSubject_rejects_builtin_grants));
        var id = SeedBuiltin(db);

        var result = await Svc(db).AddSubjectAsync(id, Guid.NewGuid());

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddSubject_then_RemoveSubject_updates_membership()
    {
        using var db = NewDb(nameof(AddSubject_then_RemoveSubject_updates_membership));
        var svc = Svc(db);
        var created = Assert.IsType<CreatedAtActionResult>((await svc.PostAsync(Create("workflow.run"))).Result);
        var id = ((PermissionGrantResponse)created.Value!).PermissionGrantId;
        var user = Guid.NewGuid();

        var added = await svc.AddSubjectAsync(id, user);
        Assert.Contains(user, added.Value!.SubjectIds);

        // Idempotent add — still a single entry.
        var addedAgain = await svc.AddSubjectAsync(id, user);
        Assert.Single(addedAgain.Value!.SubjectIds.Where(x => x == user));

        var removed = await svc.RemoveSubjectAsync(id, user);
        Assert.DoesNotContain(user, removed.Value!.SubjectIds);
    }

    [Fact]
    public async Task Delete_soft_deletes_and_hides_from_reads()
    {
        using var db = NewDb(nameof(Delete_soft_deletes_and_hides_from_reads));
        var svc = Svc(db);
        var created = Assert.IsType<CreatedAtActionResult>((await svc.PostAsync(Create("workflow.run"))).Result);
        var id = ((PermissionGrantResponse)created.Value!).PermissionGrantId;

        await svc.DeleteAsync(id);

        Assert.IsType<NotFoundObjectResult>((await svc.GetByIdAsync(id)).Result);
    }

    [Fact]
    public void Capabilities_endpoint_lists_the_whole_catalogue()
    {
        using var db = NewDb(nameof(Capabilities_endpoint_lists_the_whole_catalogue));
        var controller = new PermissionGrantController(Svc(db));

        var action = controller.Capabilities();

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var list = Assert.IsAssignableFrom<IEnumerable<CapabilityInfo>>(ok.Value).ToList();
        Assert.Equal(CapabilityCatalog.All.Count, list.Count);
    }
}
