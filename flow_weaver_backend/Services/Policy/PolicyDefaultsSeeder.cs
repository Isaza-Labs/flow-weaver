using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Policy;

// Seeds the default qa→production gate when there isn't one already.
// Replaces the hardcoded 48h check that used to live in
// PromotionService — if an admin removes/edits this gate, the
// behavior follows their decision, not a constant.
//
// Idempotent: looks up by Name. Safe to call on every startup. Uses a
// deterministic Name so nobody sees two copies after migration replays.
public static class PolicyDefaultsSeeder
{
    public const string DefaultPromotionGateName = "default.qa_to_production";

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ruleJson = JsonSerializer.Serialize(new
        {
            action = "gate",
            on = "promote",
            from = "qa",
            to = "production",
            reason = "qa validation required",
            require = new object[]
            {
                new
                {
                    type = "last_successful_run_within",
                    days = 2,
                    scope = "this_workflow",
                },
            },
        });
        var rule = JsonDocument.Parse(ruleJson).RootElement;

        var now = DateTime.UtcNow;
        var seeded = 0;
        var exists = await db.Policies.AsNoTracking().AnyAsync(
            p => p.Name == DefaultPromotionGateName);
        if (exists) return;

        db.Policies.Add(new Models.Policy
        {
            PolicyId = Guid.NewGuid(),
            Name = DefaultPromotionGateName,
            Description = "Default gate: promotion to production requires a successful qa run in the last 2 days. Edit or replace under /policies.",
            Rule = rule,
            Enabled = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        seeded++;


        if (seeded > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation(
                "policy.defaults.seeded gate={GateName} seeded={Count}",
                DefaultPromotionGateName, seeded);
        }
    }
}
