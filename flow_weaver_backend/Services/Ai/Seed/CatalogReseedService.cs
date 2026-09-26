using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;
using PromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using ApiSpecModel = flow_weaver_backend.Models.AiApiSpec;

namespace flow_weaver_backend.Services.Ai.Seed;

// Forces a re-read of the /Skills and /Specs directories and upserts
// every file into the catalog rows, overwriting content that already
// exists. Intended for the admin-only "Reseed from disk" action in
// /ai/skills and /ai/specs.
//
// Difference from CatalogSeedService:
//   Seed   → file-level idempotent; never overwrites existing rows.
//            Meant for first boot.
//   Reseed → overwrite-by-name; brings the rows back in sync with
//            the shipped .md / .yaml files when those are updated.
//            Invalidates the SkillPromptLoader and IApiSpecIndex caches.
//
// Custom rows (filenames not present on disk) are left alone — reseed
// is additive/upsert, never destructive.
//
// Unlike the boot sync, this DOES overwrite an edited shipped row: an admin
// pressing the button is asking for the shipped version. It records the
// shipped hash (ShippedCatalog) so later boots treat the row as unedited
// and keep it moving with new releases.
public sealed class CatalogReseedService
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace",
    };

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ICurrentUser _caller;
    private readonly ISkillPromptLoader _skills;
    private readonly IApiSpecIndex _specs;
    private readonly ILogger<CatalogReseedService> _logger;

    public CatalogReseedService(
        AppDbContext db,
        IWebHostEnvironment env,
        ICurrentUser caller,
        ISkillPromptLoader skills,
        IApiSpecIndex specs,
        ILogger<CatalogReseedService> logger)
    {
        _db = db;
        _env = env;
        _caller = caller;
        _skills = skills;
        _specs = specs;
        _logger = logger;
    }

    public async Task<ReseedResult> ReseedSkillsAsync(CancellationToken ct)
    {
        var dir = Path.Combine(_env.ContentRootPath, "Skills");
        if (!Directory.Exists(dir))
            return new ReseedResult { Imported = 0, Updated = 0, Detail = $"no {dir}" };

        var files = Directory.GetFiles(dir, "*.md");
        if (files.Length == 0) return new ReseedResult { Imported = 0, Updated = 0 };

        var existing = await _db.AiPromptSkills
            
            .ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, ct);

        var now = DateTime.UtcNow;
        var imported = 0;
        var updated = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var content = await File.ReadAllTextAsync(file, ct);

            var hash = ShippedCatalog.Hash(content);
            if (existing.TryGetValue(name, out var row))
            {
                if (row.Content == content)
                {
                    row.ShippedContentHash = hash;
                    continue;
                }
                row.Content = content;
                row.ShippedContentHash = hash;
                row.UpdatedAt = now;
                row.IsActive = true;
                updated++;
            }
            else
            {
                _db.AiPromptSkills.Add(new PromptSkillModel
                {
                    AiPromptSkillId = Guid.NewGuid(),
                    Name = name,
                    Content = content,
                    ShippedContentHash = hash,
                    SortOrder = name.Equals("base.md", StringComparison.OrdinalIgnoreCase) ? 0 : 100,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                imported++;
            }
        }

        // Always saved: a row whose content already matched may still have
        // needed its hash recorded.
        await _db.SaveChangesAsync(ct);
        if (imported > 0 || updated > 0)
        {
            _skills.Invalidate();
            _logger.LogInformation(
                "catalog.reseed.skills imported={Imported} updated={Updated}",
                imported, updated);
        }

        return new ReseedResult { Imported = imported, Updated = updated };
    }

    public async Task<ReseedResult> ReseedSpecsAsync(CancellationToken ct)
    {
        var dir = Path.Combine(_env.ContentRootPath, "Specs");
        if (!Directory.Exists(dir))
            return new ReseedResult { Imported = 0, Updated = 0, Detail = $"no {dir}" };

        var files = Directory.GetFiles(dir, "*.yaml")
            .Concat(Directory.GetFiles(dir, "*.yml"))
            .ToArray();
        if (files.Length == 0) return new ReseedResult { Imported = 0, Updated = 0 };

        var existing = await _db.AiApiSpecs
            
            .ToDictionaryAsync(s => s.Api, StringComparer.OrdinalIgnoreCase, ct);

        var now = DateTime.UtcNow;
        var imported = 0;
        var updated = 0;

        foreach (var file in files)
        {
            var api = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var content = await File.ReadAllTextAsync(file, ct);
            var opCount = SafeCountOps(content);

            var hash = ShippedCatalog.Hash(content);
            if (existing.TryGetValue(api, out var row))
            {
                if (row.Content == content)
                {
                    row.ShippedContentHash = hash;
                    continue;
                }
                row.Content = content;
                row.ShippedContentHash = hash;
                row.OperationCount = opCount;
                row.UpdatedAt = now;
                row.IsActive = true;
                updated++;
            }
            else
            {
                _db.AiApiSpecs.Add(new ApiSpecModel
                {
                    AiApiSpecId = Guid.NewGuid(),
                    Api = api,
                    Content = content,
                    ShippedContentHash = hash,
                    OperationCount = opCount,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                imported++;
            }
        }

        await _db.SaveChangesAsync(ct);
        if (imported > 0 || updated > 0)
        {
            await _specs.ReloadAsync(ct);
            _logger.LogInformation(
                "catalog.reseed.specs imported={Imported} updated={Updated}",
                imported, updated);
        }

        return new ReseedResult { Imported = imported, Updated = updated };
    }

    private int SafeCountOps(string yaml)
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
}

public sealed class ReseedResult
{
    public int Imported { get; init; }
    public int Updated { get; init; }
    public string? Detail { get; init; }
}
