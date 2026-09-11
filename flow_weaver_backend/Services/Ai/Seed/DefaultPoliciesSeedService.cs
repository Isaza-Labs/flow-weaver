using System.Text.Json;
using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;
using PolicyModel = flow_weaver_backend.Models.Policy;

namespace flow_weaver_backend.Services.Ai.Seed;

// Seeds two example guardrails — DISABLED by default. They're
// meant as onboarding templates: an admin looking at /policies sees
// working examples, flips Enabled=true on the ones that make sense for
// their org, and customises the rules. Shipping them disabled avoids
// breaking existing workflows on upgrade.
public static class DefaultPoliciesSeedService
{
    private sealed record PolicyTemplate(string Name, string Description, string RuleJson);

    private static readonly PolicyTemplate[] Templates =
    {
        new(
            "example-no-ssh-in-production",
            "Example: block workflows that use SSH in production. Enable + customise "
            + "to match your change-management policy.",
            """
            {
              "action": "deny",
              "reason": "SSH en production requires an approved change window.",
              "when": {
                "env": ["production"],
                "snippet_type": ["ssh"]
              }
            }
            """),
        new(
            "example-no-bgp-keywords-without-approval",
            "Example: block workflow creation when the description mentions risky "
            + "operations (bgp, reload, shutdown) without an explicit change-window tag.",
            """
            {
              "action": "deny",
              "reason": "High-impact keyword detected — add 'aprobado' or 'ventana de cambio' to the description if this is an approved change.",
              "when": {
                "action": ["create", "update"],
                "description_contains": ["bgp ", "reload ", "shutdown "]
              }
            }
            """),
    };

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Policies
            .Select(p => p.Name)
            .ToListAsync();
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var inserted = 0;
        var now = DateTime.UtcNow;
        foreach (var tpl in Templates)
        {
            if (existingSet.Contains(tpl.Name)) continue;
            db.Policies.Add(new PolicyModel
            {
                PolicyId = Guid.NewGuid(),
                Name = tpl.Name,
                Description = tpl.Description,
                Rule = JsonDocument.Parse(tpl.RuleJson).RootElement,
                Enabled = false, // Disabled by default — admin enables after review.
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            inserted++;
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Seeded {Count} example policy template(s) (all disabled)",
                inserted);
        }

    }
}
