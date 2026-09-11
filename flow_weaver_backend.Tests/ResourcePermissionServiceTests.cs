using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

public class ResourcePermissionServiceTests
{
    private readonly FakeUser _caller = new();

    private static ResourcePermissionService NewSvc(AppDbContext db, FakeUser user)
        => new(new ResourcePermissionRepository(db), new UserRepository(db), user, new FakeAudit(),
               NullLogger<ResourcePermissionService>.Instance);

    private static Guid SeedUser(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Set<User>().Add(new User
        {
            UserId = id,
            Username = "grantee",
            Email = "grantee@example.com",
            PasswordHash = "x",
            Role = "editor",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task List_unknown_resource_type_throws()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(db, _caller).ListAsync("bogus", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task List_known_type_empty()
    {
        using var db = TestDb.NewContext();
        var list = await NewSvc(db, _caller).ListAsync("workflow", Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(list);
    }

    [Fact]
    public async Task Grant_unknown_resource_type_throws()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(db, _caller).GrantAsync("bogus", Guid.NewGuid(),
                new GrantResourcePermissionRequest { SubjectType = "user", SubjectId = Guid.NewGuid(), Role = "editor" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Grant_unknown_role_throws()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSvc(db, _caller).GrantAsync("workflow", Guid.NewGuid(),
                new GrantResourcePermissionRequest { SubjectType = "user", SubjectId = Guid.NewGuid(), Role = "emperor" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Grant_then_list_roundtrip()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var userId = SeedUser(db);
        var resourceId = Guid.NewGuid();

        var granted = await svc.GrantAsync("workflow", resourceId,
            new GrantResourcePermissionRequest { SubjectType = "user", SubjectId = userId, Role = "editor" },
            CancellationToken.None);
        Assert.NotNull(granted);

        var list = await svc.ListAsync("workflow", resourceId, CancellationToken.None);
        Assert.Single(list);
    }
}
