using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Permission;

// Boot-time seed of the built-in permission grants plus the backfill of
// existing users into them, mirroring each user's legacy role.
// Idempotent — safe on every startup. Phase 1 of plan_rbac_granular.md; runs
// alongside the other always-on seeders in Program.cs.
public static class PermissionGrantSeeder
{
    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<IBuiltinGrantSync>();

        var users = 0;
        await sync.EnsureGrantsAsync();

        var roster = await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .Select(u => new { u.UserId, u.Role })
            .ToListAsync();

        foreach (var u in roster)
        {
            await sync.SyncUserAsync(u.UserId, u.Role);
            users++;
        }


        logger.LogInformation(
            "permission.grants.seeded users={Users}", users);
    }
}
