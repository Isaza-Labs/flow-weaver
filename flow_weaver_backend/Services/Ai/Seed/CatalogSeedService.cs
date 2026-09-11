using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;
using PromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using ApiSpecModel = flow_weaver_backend.Models.AiApiSpec;

namespace flow_weaver_backend.Services.Ai.Seed;

// One-shot bootstrap for the prompt-skills / api-specs tables. When
// there are no rows yet, imports the default .md and .yaml files
// shipped under /Skills and /Specs as the starting catalog. After
// this, DB is the source of truth — admins upload via the
// /api/admin/* endpoints to diverge from the shipped files.
//
// Runs at boot in Program.cs, after migrations and after DevSeedService
// (which is responsible for creating the default admin user in dev).
// Idempotent: existing rows are left alone so a deploy never overwrites
// operator-authored content.
public static class CatalogSeedService
{
    private const string BaseFileName = "base.md";

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace",
    };

    public static async Task SeedAsync(
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment env,
        ILogger logger,
        CancellationToken ct = default)
    {
        var skillsDir = Path.Combine(env.ContentRootPath, "Skills");
        var specsDir = Path.Combine(env.ContentRootPath, "Specs");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await SeedPromptSkillsAsync(db, skillsDir, logger, ct);
        await SeedApiSpecsAsync(db, specsDir, logger, ct);

        // The file-level idempotency checks above should prevent duplicates,
        // but a row surviving from a prior failed boot (e.g. a partially
        // broken spec that was inserted before we added better validation)
        // can still collide with the Api / Name unique indexes.
        // We'd rather swallow that than take down the whole app
        // on startup — the row already exists, catalog is in intended state.
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            logger.LogWarning(ex,
                "Catalog seed: duplicate row detected during save; continuing boot. "
                + "The affected skill/spec already exists — no data lost.");
            // Detach anything we tried to add so the context is clean for
            // the rest of the startup pipeline. Without this, subsequent
            // SaveChangesAsync calls would re-try the same inserts and
            // loop on the same exception.
            foreach (var entry in db.ChangeTracker.Entries().ToList())
            {
                if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            }
        }
    }

    // Postgres raises SQLSTATE 23505 for unique constraint violations.
    // EF wraps the Npgsql exception, so we walk the inner chain.
    private static bool IsUniqueViolation(Exception ex)
    {
        for (var cur = (Exception?)ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is Npgsql.PostgresException pg && pg.SqlState == "23505") return true;
        }
        return false;
    }

    private static async Task SeedPromptSkillsAsync(
        AppDbContext db, string directory, ILogger logger, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogInformation(
                "No /Skills directory to seed from; skipping");
            return;
        }

        var files = Directory.GetFiles(directory, "*.md");
        if (files.Length == 0) return;

        // File-level idempotency: we only insert skills whose filename
        // isn't already present. That way an operator who hand-edited
        // base.md keeps those changes, but new .md files shipped with
        // an update (e.g. the new domain skills) land on boot.
        var existing = await db.AiPromptSkills
            .Select(s => s.Name)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var inserted = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (existingSet.Contains(name)) continue;

            var content = await File.ReadAllTextAsync(file, ct);
            db.AiPromptSkills.Add(new PromptSkillModel
            {
                AiPromptSkillId = Guid.NewGuid(),
                Name = name,
                Content = content,
                // base.md leads; everything else sorts after at 100.
                SortOrder = name.Equals(BaseFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 100,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = null,
            });
            inserted++;
        }

        if (inserted > 0)
            logger.LogInformation(
                "Seeded {Count} new prompt skill row(s) from {Dir}",
                inserted, directory);
    }

    private static async Task SeedApiSpecsAsync(
        AppDbContext db, string directory, ILogger logger, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogInformation(
                "No /Specs directory to seed from; skipping");
            return;
        }

        var files = Directory.GetFiles(directory, "*.yaml")
            .Concat(Directory.GetFiles(directory, "*.yml"))
            .ToArray();
        if (files.Length == 0) return;

        // Same file-level idempotency as skills: the `Api` column (filename
        // stem) is the unique lookup key. Admin edits via
        // the UI stay intact; new .yaml files ship in on next boot.
        var existing = await db.AiApiSpecs
            .Select(s => s.Api)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var inserted = 0;
        foreach (var file in files)
        {
            var api = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (existingSet.Contains(api)) continue;

            var content = await File.ReadAllTextAsync(file, ct);
            int opCount;
            try { opCount = CountOperations(content); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Seed: failed to count ops for {Api}; inserting with 0", api);
                opCount = 0;
            }

            db.AiApiSpecs.Add(new ApiSpecModel
            {
                AiApiSpecId = Guid.NewGuid(),
                Api = api,
                Content = content,
                OperationCount = opCount,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = null,
            });
            inserted++;
        }

        if (inserted > 0)
            logger.LogInformation(
                "Seeded {Count} new api spec row(s) from {Dir}",
                inserted, directory);
    }

    private static int CountOperations(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
            || pathsNode is not YamlMappingNode paths)
        {
            return 0;
        }

        int count = 0;
        foreach (var pathEntry in paths)
        {
            if (pathEntry.Value is not YamlMappingNode methods) continue;
            foreach (var methodEntry in methods)
            {
                if (methodEntry.Key is YamlScalarNode methodScalar
                    && HttpMethods.Contains(methodScalar.Value ?? string.Empty))
                {
                    count++;
                }
            }
        }
        return count;
    }
}
