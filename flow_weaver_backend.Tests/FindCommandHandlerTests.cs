using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Verifies find_command ranks the vendor_commands catalog by
// task wording — the proactive "retrieve the command" path that keeps the
// agent from transposing IOS syntax onto Nokia and other vendors. The
// LLDP case is the canonical trap: SR Linux uses `show system lldp
// neighbor`, not the IOS `show lldp neighbors`.
public class FindCommandHandlerTests
{
    private static readonly FakeUser Caller = new();

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static void AddCmd(AppDbContext db, string value, string desc, string intent) =>
        db.VendorCommands.Add(new VendorCommand
        {
            VendorCommandId = Guid.NewGuid(),
            DeviceType = "nokia_srl",
            VendorFamily = "nokia",
            Kind = VendorCommand.KindExact,
            Value = value,
            Description = desc,
            Intent = intent,
            Source = VendorCommand.SourceSeed,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    private static void SeedSrl(AppDbContext db)
    {
        AddCmd(db, "show system lldp neighbor", "LLDP neighbor table, the show lldp neighbors equivalent.", "read");
        AddCmd(db, "show interface brief", "Brief interface status, admin and operational state.", "read");
        AddCmd(db, "show network-instance default route-table summary", "Routing table summary for the default VRF.", "read");
        AddCmd(db, "show version", "Software version and platform summary.", "read");
        AddCmd(db, "set / system snmp access-group ag1 community-entry ce1 community public", "Configure or change the SNMP read community string under an access-group community-entry.", "write");
        db.SaveChanges();
    }

    private static void SeedReadsOnly(AppDbContext db)
    {
        AddCmd(db, "show system lldp neighbor", "LLDP neighbor table.", "read");
        AddCmd(db, "show version", "Software version and platform summary.", "read");
        db.SaveChanges();
    }

    private static async Task<JsonElement> Run(AppDbContext db, object args)
    {
        var handler = new FindCommandHandler(new VendorCommandRepository(db), Caller, NullLogger<FindCommandHandler>.Instance);
        return await handler.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);
    }

    [Fact]
    public async Task Retrieves_lldp_command_for_srl_by_task()
    {
        using var db = NewDb(nameof(Retrieves_lldp_command_for_srl_by_task));
        SeedSrl(db);

        var result = await Run(db, new { device_type = "nokia_srl", task = "show lldp neighbors" });

        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.True(result.GetProperty("semantic_match").GetBoolean());
        var top = result.GetProperty("candidates")[0];
        Assert.Equal("show system lldp neighbor", top.GetProperty("command").GetString());
        Assert.Equal("read", top.GetProperty("intent").GetString());
    }

    [Fact]
    public async Task Falls_back_to_browse_when_no_overlap()
    {
        using var db = NewDb(nameof(Falls_back_to_browse_when_no_overlap));
        SeedSrl(db);

        var result = await Run(db, new { device_type = "nokia_srl", task = "quantum teleportation flux" });

        Assert.False(result.GetProperty("semantic_match").GetBoolean());
        Assert.True(result.GetProperty("candidates").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Reports_empty_for_unknown_device_type()
    {
        using var db = NewDb(nameof(Reports_empty_for_unknown_device_type));
        SeedSrl(db);

        var result = await Run(db, new { device_type = "acme_os", task = "show version" });

        Assert.False(result.GetProperty("found").GetBoolean());
        Assert.Equal(0, result.GetProperty("catalog_total").GetInt32());
    }

    [Fact]
    public async Task Returns_write_command_for_config_task()
    {
        using var db = NewDb(nameof(Returns_write_command_for_config_task));
        SeedSrl(db);

        var result = await Run(db, new { device_type = "nokia_srl", task = "configure snmp community", intent = "write" });

        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.True(result.GetProperty("semantic_match").GetBoolean());
        var top = result.GetProperty("candidates")[0];
        Assert.Equal("write", top.GetProperty("intent").GetString());
        Assert.Contains("snmp", top.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Refuses_read_fallback_for_write_task_without_write_commands()
    {
        using var db = NewDb(nameof(Refuses_read_fallback_for_write_task_without_write_commands));
        SeedReadsOnly(db);

        // No explicit intent — auto-detected as write from "configure".
        var result = await Run(db, new { device_type = "nokia_srl", task = "configure snmp community" });

        Assert.False(result.GetProperty("found").GetBoolean());
        Assert.Contains("invent", result.GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
