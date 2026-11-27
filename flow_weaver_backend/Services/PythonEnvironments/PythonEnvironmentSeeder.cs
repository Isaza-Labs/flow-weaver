using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.PythonEnvironments;

// Seeds `ssh-legacy` — the SSH-fallback environment — on a database that has
// NO python environment rows at all (active or deleted), so the case that
// motivated environments works out of the box:
//
//   paramiko 5.0 removed the `ssh-rsa` host-key algorithm. A device whose sshd
//   only offers ssh-rsa (seen on Extreme EXOS with OpenSSH_8.1) fails with
//   "Incompatible ssh peer (no acceptable host key)" under the image's
//   paramiko. ssh-legacy pins paramiko < 5 (resolves to 4.x, which still
//   negotiates ssh-rsa) and is flagged as the SSH fallback, so the SSH runner
//   retries such a failure once with it.
//
// "No rows at all" rather than "no row named ssh-legacy": an admin who deleted
// or renamed it made a decision, and a restart must not undo it.
//
// The id is fixed, so two processes seeding the same empty table at once
// collide on the primary key instead of creating two fallbacks.
public static class PythonEnvironmentSeeder
{
    public const string SshLegacyName = "ssh-legacy";
    public static readonly Guid SshLegacyId = new("5a5e1e6a-0000-4000-8000-000000000001");

    public const string SshLegacyRequirements =
        "netmiko>=4,<5\n"
        + "paramiko>=3.5,<5\n"
        + "textfsm>=1.1\n"
        + "ntc-templates>=4\n";

    public const string SshLegacyDescription =
        "SSH runner environment for devices that only offer the ssh-rsa host key "
        + "(e.g. Extreme EXOS / OpenSSH 8.1). paramiko 5.0 removed ssh-rsa, so the image's "
        + "paramiko fails with \"Incompatible ssh peer (no acceptable host key)\"; this "
        + "environment pins paramiko < 5. It is the SSH fallback: an ssh step that hits that "
        + "error is retried once with it. Select it on a device to use it directly.";

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.PythonEnvironments.AsNoTracking().AnyAsync(ct)) return;

        var now = DateTime.UtcNow;
        db.PythonEnvironments.Add(new PythonEnvironment
        {
            PythonEnvironmentId = SshLegacyId,
            Name = SshLegacyName,
            Description = SshLegacyDescription,
            PythonVersion = null,
            Requirements = SshLegacyRequirements,
            IsSshFallback = true,
            Status = PythonEnvironment.StatusPending,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "python.env.seed name={Name} — SSH fallback environment created (pending build)", SshLegacyName);
        }
        catch (DbUpdateException ex)
        {
            // Another process seeded it first — the fixed id collided.
            logger.LogInformation(ex, "python.env.seed skipped — already seeded by another process");
        }
    }
}
