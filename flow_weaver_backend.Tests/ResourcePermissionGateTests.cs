using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// `HasAtLeastAsync` is the function every per-resource-gated endpoint calls,
// so its answers ARE the authorization model: a wrong `true` hands someone
// else's workflow to a caller, a wrong `false` locks an admin out.
//
// The global RBAC floor is deliberately layered on top of the per-resource
// grants — admin bypasses everything, operator covers up to runner, and only
// editor/owner require an explicit grant.
public class ResourcePermissionGateTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeUser Caller { get; set; }
            = new() { UserId = User, Roles = new[] { "viewer" } };

        public ResourcePermissionService Build() => new(
            new ResourcePermissionRepository(Db),
            new UserRepository(Db),
            Caller,
            new FakeAudit(),
            NullLogger<ResourcePermissionService>.Instance);

        public Guid SeedUser(string username = "alice")
        {
            var id = Guid.NewGuid();
            Db.Users.Add(new User
            {
                UserId = id,
                Username = username,
                Email = username + "@test",
                PasswordHash = "x",
                Role = "operator",
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void SeedGrant(
            Guid resourceId, string role, Guid? subjectId = null,
            string resourceType = "workflow", bool active = true)
        {
            Db.ResourcePermissions.Add(new ResourcePermission
            {
                ResourcePermissionId = Guid.NewGuid(),
                ResourceType = resourceType,
                ResourceId = resourceId,
                SubjectType = "user",
                SubjectId = subjectId ?? User,
                Role = role,
                GrantedAt = DateTime.UtcNow,
                IsActive = active,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static FakeUser WithRoles(params string[] roles)
        => new() { UserId = User, Roles = roles };

    private static Task<bool> Check(
        Fixture f, string requiredRole, Guid resourceId, string resourceType = ResourceTypes.Workflow)
        => f.Build().HasAtLeastAsync(resourceType, resourceId, requiredRole, default);

    // ─── Role ranking ───────────────────────────────────────────────────

    [Theory]
    [InlineData("viewer", 0)]
    [InlineData("runner", 1)]
    [InlineData("editor", 2)]
    [InlineData("owner", 3)]
    public void Roles_AreRankedWeakestFirst(string role, int expected)
    {
        Assert.Equal(expected, ResourceRoles.Rank(role));
    }

    [Fact]
    public void Roles_RankingIsCaseInsensitive()
    {
        Assert.Equal(ResourceRoles.Rank("owner"), ResourceRoles.Rank("OWNER"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("superuser")]
    public void Roles_AnUnknownRoleRanksBelowEverything(string role)
    {
        Assert.Equal(-1, ResourceRoles.Rank(role));
    }

    // ─── The global floor ───────────────────────────────────────────────

    // Admin bypasses the whole per-resource model — the documented escape
    // hatch that keeps an installation from locking itself out.
    [Theory]
    [InlineData("viewer")]
    [InlineData("runner")]
    [InlineData("editor")]
    [InlineData("owner")]
    public async Task Check_AdminPassesEverything(string required)
    {
        using var f = new Fixture { Caller = WithRoles("admin") };

        Assert.True(await Check(f, required, Guid.NewGuid()));
    }

    // Operator covers up to runner globally, but editor/owner still need an
    // explicit grant — otherwise every operator could rewrite every workflow.
    [Theory]
    [InlineData("viewer", true)]
    [InlineData("runner", true)]
    [InlineData("editor", false)]
    [InlineData("owner", false)]
    public async Task Check_OperatorCoversUpToRunnerOnly(string required, bool expected)
    {
        using var f = new Fixture { Caller = WithRoles("operator") };

        Assert.Equal(expected, await Check(f, required, Guid.NewGuid()));
    }

    [Fact]
    public async Task Check_AnyAuthenticatedCallerSatisfiesViewer()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };

        Assert.True(await Check(f, ResourceRoles.Viewer, Guid.NewGuid()));
    }

    // ─── Per-resource grants ────────────────────────────────────────────

    [Fact]
    public async Task Check_AGrantAtTheRequiredRolePasses()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Editor);

        Assert.True(await Check(f, ResourceRoles.Editor, resource));
    }

    [Fact]
    public async Task Check_AStrongerGrantSatisfiesAWeakerRequirement()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Owner);

        Assert.True(await Check(f, ResourceRoles.Editor, resource));
    }

    [Fact]
    public async Task Check_AWeakerGrantDoesNotSatisfyAStrongerRequirement()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Runner);

        Assert.False(await Check(f, ResourceRoles.Owner, resource));
    }

    [Fact]
    public async Task Check_AGrantOnAnotherResourceDoesNotCarryOver()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        f.SeedGrant(Guid.NewGuid(), ResourceRoles.Owner);

        Assert.False(await Check(f, ResourceRoles.Editor, Guid.NewGuid()));
    }

    [Fact]
    public async Task Check_AGrantForAnotherSubjectDoesNotApply()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Owner, subjectId: Guid.NewGuid());

        Assert.False(await Check(f, ResourceRoles.Editor, resource));
    }

    [Fact]
    public async Task Check_ARevokedGrantNoLongerApplies()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Owner, active: false);

        Assert.False(await Check(f, ResourceRoles.Editor, resource));
    }

    // A grant recorded under a different resource type is a different
    // namespace entirely — ids can collide across tables.
    [Fact]
    public async Task Check_AGrantOnAnotherResourceTypeDoesNotApply()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Owner, resourceType: "integration");

        Assert.False(await Check(f, ResourceRoles.Editor, resource, ResourceTypes.Workflow));
    }

    // An unknown resource type or role is a caller-side programming error;
    // the safe answer is deny, not throw.
    [Fact]
    public async Task Check_AnUnknownResourceTypeIsDeniedEvenForAdmin()
    {
        using var f = new Fixture { Caller = WithRoles("admin") };

        Assert.False(await Check(f, ResourceRoles.Viewer, Guid.NewGuid(), "spaceship"));
    }

    [Fact]
    public async Task Check_AnUnknownRequiredRoleIsDeniedEvenForAdmin()
    {
        using var f = new Fixture { Caller = WithRoles("admin") };

        Assert.False(await Check(f, "superuser", Guid.NewGuid()));
    }

    [Fact]
    public async Task Check_TheStrongestOfSeveralGrantsWins()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Viewer);
        f.SeedGrant(resource, ResourceRoles.Owner);

        Assert.True(await Check(f, ResourceRoles.Owner, resource));
    }

    [Fact]
    public async Task Check_ResourceTypeMatchingIsCaseInsensitive()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var resource = Guid.NewGuid();
        f.SeedGrant(resource, ResourceRoles.Owner);

        Assert.True(await Check(f, ResourceRoles.Editor, resource, "WORKFLOW"));
    }

    // ─── Grant / revoke round trip ──────────────────────────────────────

    private static GrantResourcePermissionRequest GrantRequest(Guid subjectId, string role = "editor")
        => new() { SubjectType = "user", SubjectId = subjectId, Role = role };

    [Fact]
    public async Task Grant_RecordsWhoGrantedIt()
    {
        using var f = new Fixture();
        var subject = f.SeedUser("alice");

        var response = await f.Build().GrantAsync(
            ResourceTypes.Workflow, Guid.NewGuid(), GrantRequest(subject), default);

        Assert.Equal("editor", response.Role);
        Assert.Equal(subject, response.SubjectId);
        Assert.Equal(User, Assert.Single(f.Db.ResourcePermissions).GrantedBy);
    }

    // Re-granting the same (resource, subject, role) returns the existing row
    // — the UI's grant button is not idempotency-aware.
    [Fact]
    public async Task Grant_IsIdempotent()
    {
        using var f = new Fixture();
        var subject = f.SeedUser();
        var resource = Guid.NewGuid();

        var first = await f.Build().GrantAsync(ResourceTypes.Workflow, resource, GrantRequest(subject), default);
        var second = await f.Build().GrantAsync(ResourceTypes.Workflow, resource, GrantRequest(subject), default);

        Assert.Equal(first.ResourcePermissionId, second.ResourcePermissionId);
        Assert.Single(f.Db.ResourcePermissions);
    }

    [Fact]
    public async Task Grant_OnlyUserSubjectsAreSupportedToday()
    {
        using var f = new Fixture();
        var subject = f.SeedUser();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Build().GrantAsync(
            ResourceTypes.Workflow, Guid.NewGuid(),
            new GrantResourcePermissionRequest { SubjectType = "group", SubjectId = subject, Role = "editor" },
            default));

        Assert.Empty(f.Db.ResourcePermissions);
    }

    [Fact]
    public async Task Grant_RequiresASubjectId()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Build().GrantAsync(
            ResourceTypes.Workflow, Guid.NewGuid(), GrantRequest(Guid.Empty), default));
    }

    // The listing resolves usernames so the UI can show who holds what
    // without a second round-trip.
    [Fact]
    public async Task List_ResolvesTheSubjectUsername()
    {
        using var f = new Fixture();
        var subject = f.SeedUser("alice");
        var resource = Guid.NewGuid();
        await f.Build().GrantAsync(ResourceTypes.Workflow, resource, GrantRequest(subject), default);

        var rows = await f.Build().ListAsync(ResourceTypes.Workflow, resource, default);

        Assert.Equal("alice", Assert.Single(rows).SubjectUsername);
    }

    [Fact]
    public async Task List_OnlyReturnsGrantsForTheAskedResource()
    {
        using var f = new Fixture();
        var subject = f.SeedUser();
        var resource = Guid.NewGuid();
        await f.Build().GrantAsync(ResourceTypes.Workflow, resource, GrantRequest(subject), default);
        await f.Build().GrantAsync(ResourceTypes.Workflow, Guid.NewGuid(), GrantRequest(subject), default);

        Assert.Single(await f.Build().ListAsync(ResourceTypes.Workflow, resource, default));
    }

    // Revoking is a soft delete so the audit trail stays resolvable, and the
    // grant stops counting immediately.
    [Fact]
    public async Task Revoke_DeactivatesTheGrantAndRemovesItsEffect()
    {
        using var f = new Fixture { Caller = WithRoles("viewer") };
        var subject = f.SeedUser();
        var resource = Guid.NewGuid();
        var granted = await f.Build().GrantAsync(
            ResourceTypes.Workflow, resource,
            new GrantResourcePermissionRequest { SubjectType = "user", SubjectId = User, Role = "editor" },
            default);
        Assert.True(await Check(f, ResourceRoles.Editor, resource));

        await f.Build().RevokeAsync(granted.ResourcePermissionId, default);

        Assert.False(f.Db.ResourcePermissions.Single().IsActive);
        Assert.False(await Check(f, ResourceRoles.Editor, resource));
        Assert.Empty(await f.Build().ListAsync(ResourceTypes.Workflow, resource, default));
    }

    [Fact]
    public async Task Revoke_AnUnknownIdIsANoOp()
    {
        using var f = new Fixture();

        await f.Build().RevokeAsync(Guid.NewGuid(), default);

        Assert.Empty(f.Db.ResourcePermissions);
    }
}
