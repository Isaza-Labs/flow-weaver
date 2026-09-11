using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Permission.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Phase 1 of the RBAC-granular refactor (plan_rbac_granular.md): the built-in
// operator/viewer grants and the dual-write that mirrors User.Role into them.
// These pin the "no behaviour change on migration" guarantee — after seeding,
// every user holds exactly the capabilities their legacy role granted.
public class BuiltinGrantSyncTests
{

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static BuiltinGrantSync NewSync(AppDbContext db) =>
        new(db, NullLogger<BuiltinGrantSync>.Instance);

    private static async Task<PermissionGrant> Grant(AppDbContext db, string name) =>
        await db.PermissionGrants.SingleAsync(g => g.Name == name);

    [Fact]
    public async Task Ensure_creates_two_builtin_bundles_with_catalogue_caps()
    {
        using var db = NewDb(nameof(Ensure_creates_two_builtin_bundles_with_catalogue_caps));

        await NewSync(db).EnsureGrantsAsync();

        var all = await db.PermissionGrants.ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, g => Assert.True(g.IsBuiltIn && g.Enabled));
        Assert.All(all, g => Assert.Empty(g.SubjectIds));

        var viewer = await Grant(db, BuiltinGrantSync.ViewerGrantName);
        var op = await Grant(db, BuiltinGrantSync.OperatorGrantName);
        Assert.True(viewer.Capabilities.ToHashSet()
            .SetEquals(CapabilityCatalog.CapabilitiesForLegacyRole("viewer")));
        Assert.True(op.Capabilities.ToHashSet()
            .SetEquals(CapabilityCatalog.CapabilitiesForLegacyRole("operator")));
    }

    [Fact]
    public async Task Ensure_is_idempotent()
    {
        using var db = NewDb(nameof(Ensure_is_idempotent));
        var sync = NewSync(db);

        await sync.EnsureGrantsAsync();
        await sync.EnsureGrantsAsync();

    }

    [Fact]
    public async Task Ensure_refreshes_stale_capabilities()
    {
        using var db = NewDb(nameof(Ensure_refreshes_stale_capabilities));
        db.PermissionGrants.Add(new PermissionGrant
        {
            PermissionGrantId = Guid.NewGuid(),
            Name = BuiltinGrantSync.ViewerGrantName,
            IsBuiltIn = true,
            Enabled = true,
            Capabilities = new() { "stale.capability" },
        });
        await db.SaveChangesAsync();

        await NewSync(db).EnsureGrantsAsync();

        var viewer = await Grant(db, BuiltinGrantSync.ViewerGrantName);
        Assert.DoesNotContain("stale.capability", viewer.Capabilities);
        Assert.True(viewer.Capabilities.ToHashSet()
            .SetEquals(CapabilityCatalog.CapabilitiesForLegacyRole("viewer")));
    }

    [Fact]
    public async Task Sync_places_user_in_matching_bundle_and_moves_on_change()
    {
        using var db = NewDb(nameof(Sync_places_user_in_matching_bundle_and_moves_on_change));
        var sync = NewSync(db);
        var user = Guid.NewGuid();

        await sync.SyncUserAsync(user, "operator");
        Assert.Contains(user, (await Grant(db, BuiltinGrantSync.OperatorGrantName)).SubjectIds);
        Assert.DoesNotContain(user, (await Grant(db, BuiltinGrantSync.ViewerGrantName)).SubjectIds);

        await sync.SyncUserAsync(user, "viewer");
        Assert.Contains(user, (await Grant(db, BuiltinGrantSync.ViewerGrantName)).SubjectIds);
        Assert.DoesNotContain(user, (await Grant(db, BuiltinGrantSync.OperatorGrantName)).SubjectIds);
    }

    [Fact]
    public async Task Sync_admin_is_a_subject_of_no_builtin_bundle()
    {
        using var db = NewDb(nameof(Sync_admin_is_a_subject_of_no_builtin_bundle));
        var sync = NewSync(db);
        var user = Guid.NewGuid();

        await sync.SyncUserAsync(user, "operator");
        await sync.SyncUserAsync(user, "admin");

        Assert.DoesNotContain(user, (await Grant(db, BuiltinGrantSync.OperatorGrantName)).SubjectIds);
        Assert.DoesNotContain(user, (await Grant(db, BuiltinGrantSync.ViewerGrantName)).SubjectIds);
    }

    [Fact]
    public async Task Seeder_backfills_existing_users_by_role()
    {
        var dbName = nameof(Seeder_backfills_existing_users_by_role);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IBuiltinGrantSync, BuiltinGrantSync>();
        using var sp = services.BuildServiceProvider();

        var admin = Guid.NewGuid();
        var op = Guid.NewGuid();
        var viewer = Guid.NewGuid();

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                NewUser(admin, "admin"), NewUser(op, "operator"), NewUser(viewer, "viewer"));
            await db.SaveChangesAsync();
        }

        await PermissionGrantSeeder.SeedAsync(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var operatorGrant = await db.PermissionGrants
                .SingleAsync(g => g.Name == BuiltinGrantSync.OperatorGrantName);
            var viewerGrant = await db.PermissionGrants
                .SingleAsync(g => g.Name == BuiltinGrantSync.ViewerGrantName);

            Assert.Contains(op, operatorGrant.SubjectIds);
            Assert.Contains(viewer, viewerGrant.SubjectIds);
            // admin bypasses — subject of neither bundle.
            Assert.DoesNotContain(admin, operatorGrant.SubjectIds);
            Assert.DoesNotContain(admin, viewerGrant.SubjectIds);
            // operator is not double-listed as a viewer.
            Assert.DoesNotContain(op, viewerGrant.SubjectIds);
        }
    }

    private static User NewUser(Guid id, string role) => new()
    {
        UserId = id,
        Username = role + "-user",
        Email = role + "@acme.test",
        Role = role,
        IsActive = true,
    };
}
