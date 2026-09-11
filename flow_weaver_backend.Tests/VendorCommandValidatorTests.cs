using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Verifies VendorCommandValidator surfaces the right warnings for each
// resolution path. Uses EF Core InMemory; the registry's TTL caching
// is exercised implicitly because every test re-creates the cache by
// using a fresh provider.
public class VendorCommandValidatorTests
{
    private static readonly Guid SshSnippetId = Guid.NewGuid();
    private static readonly Guid OtherSnippetId = Guid.NewGuid();

    private static (AppDbContext db, IVendorCommandValidator validator) BuildAsync(
        string dbName,
        Action<AppDbContext>? seed = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IVendorCommandRepository, VendorCommandRepository>();
        services.AddSingleton<IVendorCommandRegistry, VendorCommandRegistry>();
        services.AddSingleton<ILogger<VendorCommandRegistry>>(NullLogger<VendorCommandRegistry>.Instance);
        services.AddSingleton<ILogger<VendorCommandValidator>>(NullLogger<VendorCommandValidator>.Instance);
        var sp = services.BuildServiceProvider();

        var db = sp.GetRequiredService<AppDbContext>();
        SeedSshSnippet(db);
        seed?.Invoke(db);
        db.SaveChanges();

        var registry = sp.GetRequiredService<IVendorCommandRegistry>();
        var validator = new VendorCommandValidator(
            new SnippetRepository(db), new RepositoryBase<Device>(db),
            registry, NullLogger<VendorCommandValidator>.Instance);
        return (db, validator);
    }

    private static void SeedSshSnippet(AppDbContext db)
    {
        db.Snippets.Add(new Snippet
        {
            SnippetId = SshSnippetId,
            Name = "ssh",
            Type = "ssh",
            TargetMode = "per_device",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        // Non-ssh snippet so we can confirm the validator skips it.
        db.Snippets.Add(new Snippet
        {
            SnippetId = OtherSnippetId,
            Name = "ping",
            Type = "ping",
            TargetMode = "per_device",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private static void SeedCatalogRow(AppDbContext db, string deviceType, string kind, string value)
    {
        db.VendorCommands.Add(new VendorCommand
        {
            VendorCommandId = Guid.NewGuid(),
            DeviceType = deviceType,
            VendorFamily = "test",
            Kind = kind,
            Value = value,
            Source = VendorCommand.SourceSeed,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private static JsonElement BuildSshNode(
        string nodeId, string? deviceType, params string[] commands)
    {
        var configOverrides = new Dictionary<string, object?>();
        if (deviceType is not null) configOverrides["device_type"] = deviceType;
        if (commands.Length == 1) configOverrides["command"] = commands[0];
        else if (commands.Length > 1) configOverrides["commands"] = commands;

        var node = new
        {
            id = nodeId,
            snippet_id = SshSnippetId.ToString(),
            config_overrides = configOverrides,
        };
        return JsonSerializer.SerializeToElement(new[] { node });
    }

    [Fact]
    public async Task Known_exact_command_produces_no_warning()
    {
        var (_, validator) = BuildAsync(nameof(Known_exact_command_produces_no_warning),
            db => SeedCatalogRow(db, "cisco_ios", "exact", "show version"));

        var nodes = BuildSshNode("ssh-1", "cisco_ios", "show version");
        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }

    [Fact]
    public async Task Pattern_match_produces_no_warning()
    {
        var (_, validator) = BuildAsync(nameof(Pattern_match_produces_no_warning),
            db => SeedCatalogRow(db, "cisco_ios", "pattern", @"^show\s+\S+(\s+\S+)*$"));

        var nodes = BuildSshNode("ssh-1", "cisco_ios", "show ip interface brief");
        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }

    [Fact]
    public async Task Unknown_command_produces_warning_with_suggestions()
    {
        var (_, validator) = BuildAsync(nameof(Unknown_command_produces_warning_with_suggestions),
            db =>
            {
                SeedCatalogRow(db, "cisco_ios", "exact", "show version");
                SeedCatalogRow(db, "cisco_ios", "exact", "show vlan");
            });

        var nodes = BuildSshNode("ssh-1", "cisco_ios", "show vesion");
        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Warnings);
        Assert.Single(result.Warnings!);
        var warning = result.Warnings![0];
        Assert.Contains("show vesion", warning);
        Assert.Contains("cisco_ios", warning);
        // 'show version' is the lowest-distance neighbour to 'show vesion'.
        Assert.Contains("show version", warning);
    }

    [Fact]
    public async Task Template_command_is_skipped()
    {
        var (_, validator) = BuildAsync(nameof(Template_command_is_skipped),
            db => SeedCatalogRow(db, "cisco_ios", "exact", "show version"));

        // Template expression — validator can't check at plan time so it
        // must NOT warn.
        var nodes = BuildSshNode("ssh-1", "cisco_ios", "{{ steps.x.output }}");
        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }

    [Fact]
    public async Task Device_type_unresolved_emits_deferred_warning()
    {
        var (_, validator) = BuildAsync(nameof(Device_type_unresolved_emits_deferred_warning),
            db => SeedCatalogRow(db, "cisco_ios", "exact", "show version"));

        // No device_type on the node, no target devices — validator can't
        // pick a catalog so it surfaces a deferred warning.
        var nodes = BuildSshNode("ssh-1", deviceType: null, "show version");
        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.NotNull(result.Warnings);
        Assert.Single(result.Warnings!);
        Assert.Contains("device_type unresolved", result.Warnings![0]);
    }

    [Fact]
    public async Task Catalog_empty_for_device_type_emits_deferred_warning()
    {
        var (_, validator) = BuildAsync(nameof(Catalog_empty_for_device_type_emits_deferred_warning));

        var nodes = BuildSshNode("ssh-1", "cisco_ios", "show version");
        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.NotNull(result.Warnings);
        Assert.Single(result.Warnings!);
        Assert.Contains("no catalog entries", result.Warnings![0]);
    }

    [Fact]
    public async Task Non_ssh_nodes_are_ignored()
    {
        var (_, validator) = BuildAsync(nameof(Non_ssh_nodes_are_ignored),
            db => SeedCatalogRow(db, "cisco_ios", "exact", "show version"));

        // ping node — even with bogus commands it should not be inspected.
        var node = new
        {
            id = "ping-1",
            snippet_id = OtherSnippetId.ToString(),
            config_overrides = new { command = "definitely not a vendor command" },
        };
        var nodes = JsonSerializer.SerializeToElement(new[] { node });

        var result = await validator.ValidateAsync(nodes, Array.Empty<Guid>(), default);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }

    [Fact]
    public async Task ValidateCommandsAsync_returns_per_command_warnings()
    {
        var (_, validator) = BuildAsync(nameof(ValidateCommandsAsync_returns_per_command_warnings),
            db =>
            {
                SeedCatalogRow(db, "cisco_ios", "exact", "show version");
                SeedCatalogRow(db, "cisco_ios", "pattern", @"^show\s+\S+(\s+\S+)*$");
            });

        var warnings = await validator.ValidateCommandsAsync(
            "cisco_ios",
            new[] { "show version", "show ip interface brief", "reboot now" },
            default);

        // First two are known (one exact, one via pattern); the third is
        // unknown and surfaces a warning.
        Assert.Single(warnings);
        Assert.Contains("reboot now", warnings[0]);
    }

    [Fact]
    public async Task Mixed_target_platforms_emit_pin_warning()
    {
        var (db, validator) = BuildAsync(nameof(Mixed_target_platforms_emit_pin_warning),
            seed =>
            {
                SeedCatalogRow(seed, "cisco_ios", "exact", "show version");
                SeedCatalogRow(seed, "juniper_junos", "exact", "show configuration");
                seed.Devices.Add(new Device
                {
                    DeviceId = Guid.NewGuid(),
                    DeviceName = "rtr1",
                    IpAddress = "10.0.0.1",
                    Platform = "cisco_ios",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                seed.Devices.Add(new Device
                {
                    DeviceId = Guid.NewGuid(),
                    DeviceName = "rtr2",
                    IpAddress = "10.0.0.2",
                    Platform = "juniper_junos",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            });

        var deviceIds = db.Devices.Select(d => d.DeviceId).ToList();
        var nodes = BuildSshNode("ssh-1", deviceType: null, "show version");
        var result = await validator.ValidateAsync(nodes, deviceIds, default);

        Assert.NotNull(result.Warnings);
        Assert.Contains(result.Warnings!, w => w.Contains("multiple platforms"));
    }
}
