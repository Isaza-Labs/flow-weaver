using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Auth;

// Creates a deterministic admin user at startup so a fresh dev database is
// immediately usable. Invoked ONLY when the app is running in
// the Development environment — never touches production DBs.
//
// The seeded credentials intentionally violate the password policy
// ("admin" is 5 chars and on the common-passwords blacklist). This works
// because the seeder hashes the password directly with PasswordHasher<User>
// instead of going through IPasswordPolicy + IAuthService, which do enforce
// the rules on the real login/bootstrap/create-user paths.
//
// Seeded credentials:
//   username = admin
//   password = admin
//   role     = admin
//
// In production these accounts are created via POST /api/auth/bootstrap
// (first-time setup) or POST /api/users (admin creates operators/viewers),
// both of which DO enforce the password policy.
public static class DevSeedService
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin";

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var now = DateTime.UtcNow;

        var adminExists = await db.Users.AnyAsync(u => u.Username == DefaultAdminUsername);
        if (adminExists)
        {
            logger.LogInformation("Dev seed: admin user already present, skipping");
            return;
        }

        var admin = new User
        {
            UserId = Guid.NewGuid(),
            Username = DefaultAdminUsername,
            Email = "admin@default.local",
            Role = "admin",
            PasswordChangedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Bypasses IPasswordPolicy on purpose — see class-level comment.
        admin.PasswordHash = hasher.HashPassword(admin, DefaultAdminPassword);

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        logger.LogWarning(
            "Dev seed: created admin user '{Username}' with password '{Password}'. " +
            "Change it before any non-local use.",
            admin.Username, DefaultAdminPassword);
    }
}
