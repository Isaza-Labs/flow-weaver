using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Services.Ai.Seed;

// Walks Skills/vendors/*.yaml and seeds the vendor_commands table
// for vendors that the in-code DefaultVendorCommandsSeedService does not
// cover. Lets product add new vendors by dropping a YAML file in the
// repo without recompiling.
//
// Idempotent by (DeviceType, Kind="exact", Value): existing
// rows keep their Value/Source, but a null Description/Intent is
// backfilled from the YAML so the semantics land on rows the in-code
// DefaultVendorCommandsSeedService seeded without them. Runs AFTER that
// service so when both define the same device_type, the in-code
// catalogue wins for the entries it covers and the YAML provides the
// gap-fill rows plus the description/intent metadata find_command reads.
public static class VendorCommandYamlSeedService
{
    private sealed class YamlVendorFile
    {
        public string DeviceType { get; set; } = string.Empty;
        public string VendorFamily { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<YamlVendorCommand> Commands { get; set; } = new();
    }

    private sealed class YamlVendorCommand
    {
        public string Command { get; set; } = string.Empty;
        public string? Intent { get; set; }
        public string? Description { get; set; }

        // "exact" (default) or "pattern". Lets a YAML carry regex
        // validation entries (e.g. parametrised `set / system snmp …`
        // config lines) next to literal commands.
        public string? Kind { get; set; }
    }

    public static async Task SeedAsync(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment env,
        ILogger logger,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(env.ContentRootPath, "Skills", "vendors");
        if (!Directory.Exists(dir))
        {
            logger.LogDebug("vendor_yaml.seed.no_directory path={Path}", dir);
            return;
        }

        var files = Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            logger.LogDebug("vendor_yaml.seed.no_files path={Path}", dir);
            return;
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var catalogs = new List<YamlVendorFile>();
        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file, ct);
                var parsed = deserializer.Deserialize<YamlVendorFile>(text);
                if (parsed is null
                    || string.IsNullOrWhiteSpace(parsed.DeviceType)
                    || parsed.Commands is null
                    || parsed.Commands.Count == 0)
                {
                    logger.LogWarning("vendor_yaml.seed.skipped file={File} reason=empty_or_invalid", file);
                    continue;
                }
                catalogs.Add(parsed);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "vendor_yaml.seed.parse_failed file={File}", file);
            }
        }

        if (catalogs.Count == 0) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var inserted = 0;
        var backfilled = 0;
        var skipped = 0;
        var now = DateTime.UtcNow;
        foreach (var cat in catalogs)
        {
            // Load existing rows (all kinds) as tracked entities so we
            // can dedup by (kind, value) and backfill Description/Intent
            // onto rows that predate those columns — e.g. the same
            // device_type seeded by the in-code catalogue, which ships
            // no semantics. New rows are still inserted as before.
            var existing = await db.VendorCommands
                .Where(v => v.DeviceType == cat.DeviceType)
                .ToListAsync(ct);
            var byKey = new Dictionary<(string Kind, string Value), VendorCommandModel>();
            foreach (var row in existing)
                byKey[(row.Kind.ToLowerInvariant(), row.Value.ToLowerInvariant())] = row;
            var seen = new HashSet<(string, string)>();

            foreach (var cmd in cat.Commands)
            {
                if (string.IsNullOrWhiteSpace(cmd.Command)) continue;
                var normalised = cmd.Command.Trim();
                var kind = string.IsNullOrWhiteSpace(cmd.Kind)
                    ? VendorCommandModel.KindExact
                    : cmd.Kind.Trim().ToLowerInvariant();
                if (kind != VendorCommandModel.KindExact && kind != VendorCommandModel.KindPattern)
                    kind = VendorCommandModel.KindExact;
                var key = (kind, normalised.ToLowerInvariant());
                var description = string.IsNullOrWhiteSpace(cmd.Description) ? null : cmd.Description.Trim();
                var intent = string.IsNullOrWhiteSpace(cmd.Intent) ? null : cmd.Intent.Trim();

                if (byKey.TryGetValue(key, out var existingRow))
                {
                    // Backfill only — never clobber a description an
                    // admin (or an earlier, richer seed) already set.
                    var changed = false;
                    if (string.IsNullOrWhiteSpace(existingRow.Description) && description is not null)
                    {
                        existingRow.Description = description;
                        changed = true;
                    }
                    if (string.IsNullOrWhiteSpace(existingRow.Intent) && intent is not null)
                    {
                        existingRow.Intent = intent;
                        changed = true;
                    }
                    if (changed) { existingRow.UpdatedAt = now; backfilled++; }
                    else skipped++;
                    continue;
                }

                // Guard against the same (kind, command) listed twice in one YAML.
                if (!seen.Add(key)) { skipped++; continue; }

                db.VendorCommands.Add(new VendorCommandModel
                {
                    VendorCommandId = Guid.NewGuid(),
                    DeviceType = cat.DeviceType,
                    VendorFamily = string.IsNullOrWhiteSpace(cat.VendorFamily)
                        ? "generic"
                        : cat.VendorFamily,
                    Kind = kind,
                    Value = normalised,
                    Description = description,
                    Intent = intent,
                    Notes = BuildNotes(cmd),
                    Source = VendorCommandModel.SourceSeed,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                inserted++;
            }
        }


        if (inserted > 0 || backfilled > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "vendor_yaml.seed.ok files={Files} inserted={Inserted} backfilled={Backfilled} skipped={Skipped}",
                files.Length, inserted, backfilled, skipped);
        }
        else
        {
            logger.LogDebug(
                "vendor_yaml.seed.noop files={Files} skipped={Skipped}",
                files.Length, skipped);
        }
    }

    // The Notes column is freeform — we stitch intent + description so
    // admins viewing the catalog under /vendor-commands can see *why*
    // the command lives in the allow-list.
    private static string? BuildNotes(YamlVendorCommand cmd)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(cmd.Intent)) parts.Add($"intent: {cmd.Intent}");
        if (!string.IsNullOrWhiteSpace(cmd.Description)) parts.Add(cmd.Description);
        return parts.Count == 0 ? null : string.Join(" — ", parts);
    }
}
