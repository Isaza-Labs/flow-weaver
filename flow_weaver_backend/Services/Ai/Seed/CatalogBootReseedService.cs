using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;
using PromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using ApiSpecModel = flow_weaver_backend.Models.AiApiSpec;

namespace flow_weaver_backend.Services.Ai.Seed;

// Boot-time skill + spec sync. Runs every time the backend starts, so a
// new release's shipped /Skills/*.md and /Specs/*.yaml reach the catalog
// without anyone clicking "Reseed from disk".
//
// A shipped update replaces a row only while the row still holds the
// shipped content the sync last gave it (ShippedCatalog.Decide). An admin
// who edits a shipped skill keeps the edit across restarts and upgrades;
// the boot logs that the row differs from the shipped file, and "Reseed
// from disk" is the explicit way to take the shipped version. The boot
// also never re-activates a row an admin deactivated — only a brand-new
// row is created active.
//
// Files no longer present on disk are NOT deleted from the DB — that
// keeps custom rows that were never shipped from disk in the first place.
//
// The runtime "Reseed from disk" button on /ai/skills + /ai/specs
// stays as the way to pick up changes inside a running container
// without a restart (e.g. ops shells in, edits a .md, reloads).
public static class CatalogBootReseedService
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace",
    };

    public static async Task ReseedAllAsync(
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment env,
        ILogger logger,
        CancellationToken ct = default)
    {
        var skillsDir = Path.Combine(env.ContentRootPath, "Skills");
        var specsDir = Path.Combine(env.ContentRootPath, "Specs");

        // Precompute disk catalogs once so the sync pass is just a DB
        // read + diff, not N file reads.
        var skillFiles = await ReadFilesAsync(skillsDir, new[] { "*.md" }, ct);
        var specFiles = await ReadFilesAsync(specsDir, new[] { "*.yaml", "*.yml" }, ct);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var skillLoader = scope.ServiceProvider.GetService<ISkillPromptLoader>();
        var specIndex = scope.ServiceProvider.GetService<IApiSpecIndex>();

        var totalSkillsImported = 0;
        var totalSkillsUpdated = 0;
        var totalSpecsImported = 0;
        var totalSpecsUpdated = 0;

        (int sImp, int sUpd) = await UpsertSkillsAsync(db, skillFiles, logger, ct);
        (int apiImp, int apiUpd) = await UpsertSpecsAsync(db, specFiles, logger, ct);
        totalSkillsImported += sImp;
        totalSkillsUpdated += sUpd;
        totalSpecsImported += apiImp;
        totalSpecsUpdated += apiUpd;

        // Cache invalidation so the next /ai/chat turn re-reads from
        // the DB instead of serving stale pre-restart content.
        if (sImp + sUpd > 0)
            skillLoader?.Invalidate();
        if ((apiImp + apiUpd) > 0 && specIndex is not null)
            await specIndex.ReloadAsync(ct);


        logger.LogInformation(
            "catalog.boot.reseed skills_inserted={SkillsInserted} skills_updated={SkillsUpdated} specs_inserted={SpecsInserted} specs_updated={SpecsUpdated}",
            totalSkillsImported, totalSkillsUpdated,
            totalSpecsImported, totalSpecsUpdated);
    }

    private static async Task<(int imported, int updated)> UpsertSkillsAsync(
        AppDbContext db, IReadOnlyList<DiskFile> files, ILogger logger, CancellationToken ct)
    {
        if (files.Count == 0) return (0, 0);

        var existing = await db.AiPromptSkills
            .ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, ct);

        var now = DateTime.UtcNow;
        var imported = 0;
        var updated = 0;
        var hashesRecorded = false;
        foreach (var file in files)
        {
            var name = file.Name;
            if (existing.TryGetValue(name, out var row))
            {
                switch (ShippedCatalog.Decide(row.Content, row.ShippedContentHash, row.CreatedBy, file.Content))
                {
                    case ShippedCatalog.Decision.RecordHash:
                        row.ShippedContentHash = ShippedCatalog.Hash(file.Content);
                        hashesRecorded = true;
                        break;
                    case ShippedCatalog.Decision.Update:
                        row.Content = file.Content;
                        row.ShippedContentHash = ShippedCatalog.Hash(file.Content);
                        row.UpdatedAt = now;
                        updated++;
                        break;
                    case ShippedCatalog.Decision.KeepEdited:
                        logger.LogInformation(
                            "catalog.boot.kept_edit kind=skill name={Name} — differs from the shipped file and was kept; Reseed from disk takes the shipped version",
                            name);
                        break;
                }
            }
            else
            {
                db.AiPromptSkills.Add(new PromptSkillModel
                {
                    AiPromptSkillId = Guid.NewGuid(),
                    Name = name,
                    Content = file.Content,
                    ShippedContentHash = ShippedCatalog.Hash(file.Content),
                    SortOrder = name.Equals("base.md", StringComparison.OrdinalIgnoreCase) ? 0 : 100,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                imported++;
            }
        }
        if (imported + updated > 0 || hashesRecorded)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Recover from a concurrent boot that already inserted
                // the same row. Detach our pending changes; the other
                // process won the race, and its content is identical
                // (both read from the same disk file).
                foreach (var entry in db.ChangeTracker.Entries().ToList())
                    if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
                return (0, 0);
            }
        }
        return (imported, updated);
    }

    private static async Task<(int imported, int updated)> UpsertSpecsAsync(
        AppDbContext db, IReadOnlyList<DiskFile> files, ILogger logger, CancellationToken ct)
    {
        if (files.Count == 0) return (0, 0);

        var existing = await db.AiApiSpecs
            .ToDictionaryAsync(s => s.Api, StringComparer.OrdinalIgnoreCase, ct);

        var now = DateTime.UtcNow;
        var imported = 0;
        var updated = 0;
        var hashesRecorded = false;
        foreach (var file in files)
        {
            var api = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();

            if (existing.TryGetValue(api, out var row))
            {
                switch (ShippedCatalog.Decide(row.Content, row.ShippedContentHash, row.CreatedBy, file.Content))
                {
                    case ShippedCatalog.Decision.RecordHash:
                        row.ShippedContentHash = ShippedCatalog.Hash(file.Content);
                        hashesRecorded = true;
                        break;
                    case ShippedCatalog.Decision.Update:
                        row.Content = file.Content;
                        row.OperationCount = SafeCountOps(file.Content);
                        row.ShippedContentHash = ShippedCatalog.Hash(file.Content);
                        row.UpdatedAt = now;
                        updated++;
                        break;
                    case ShippedCatalog.Decision.KeepEdited:
                        logger.LogInformation(
                            "catalog.boot.kept_edit kind=spec name={Name} — differs from the shipped file and was kept; Reseed from disk takes the shipped version",
                            api);
                        break;
                }
            }
            else
            {
                db.AiApiSpecs.Add(new ApiSpecModel
                {
                    AiApiSpecId = Guid.NewGuid(),
                    Api = api,
                    Content = file.Content,
                    ShippedContentHash = ShippedCatalog.Hash(file.Content),
                    OperationCount = SafeCountOps(file.Content),
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                imported++;
            }
        }
        if (imported + updated > 0 || hashesRecorded)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                foreach (var entry in db.ChangeTracker.Entries().ToList())
                    if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
                return (0, 0);
            }
        }
        return (imported, updated);
    }

    private static async Task<List<DiskFile>> ReadFilesAsync(
        string dir, IReadOnlyList<string> patterns, CancellationToken ct)
    {
        var result = new List<DiskFile>();
        if (!Directory.Exists(dir)) return result;
        foreach (var pat in patterns)
        {
            foreach (var path in Directory.GetFiles(dir, pat))
            {
                var content = await File.ReadAllTextAsync(path, ct);
                result.Add(new DiskFile(Path.GetFileName(path), content));
            }
        }
        return result;
    }

    private static int SafeCountOps(string yaml)
    {
        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count == 0
                || stream.Documents[0].RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("paths"), out var paths)
                || paths is not YamlMappingNode pathsMap)
                return 0;

            var count = 0;
            foreach (var p in pathsMap)
            {
                if (p.Value is not YamlMappingNode methods) continue;
                foreach (var m in methods)
                    if (m.Key is YamlScalarNode k && HttpMethods.Contains(k.Value ?? string.Empty))
                        count++;
            }
            return count;
        }
        catch { return 0; }
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        // Walk the inner chain — Npgsql wraps the original exception
        // and the error code we want lives on PostgresException.SqlState.
        var current = ex;
        while (current is not null)
        {
            if (current.GetType().Name == "PostgresException")
            {
                var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
                if (sqlState == "23505") return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    private readonly record struct DiskFile(string Name, string Content);
}
