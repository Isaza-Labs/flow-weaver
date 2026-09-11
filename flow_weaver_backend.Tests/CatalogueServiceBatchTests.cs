using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Scratch;
using flow_weaver_backend.Services.AIAgent;
using flow_weaver_backend.Services.Device;
using flow_weaver_backend.Services.PythonModules;
using flow_weaver_backend.Services.Skill;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AIAgentModel = flow_weaver_backend.Models.AIAgent;
using DeviceModel = flow_weaver_backend.Models.Device;
using SkillModel = flow_weaver_backend.Models.Skill;

namespace flow_weaver_backend.Tests;

// Five small catalogue services batched by cost. Two of them carry a
// non-obvious rule worth pinning: the python allow-list HARD-deletes (a soft
// delete would keep the unique index occupied and make the name unaddable
// forever), and a stdlib module is usable immediately while a pip one has to
// wait for the provisioner.
public class CatalogueServiceBatchTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    private static FakeUser Caller() => new() { UserId = User };

    private static string ErrorOf<T>(ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    // ─── SkillService ───────────────────────────────────────────────────

    private static SkillService Skills(AppDbContext db)
        => new(new RepositoryBase<SkillModel>(db), Caller(), NullLogger<SkillService>.Instance);

    private static Guid SeedSkill(AppDbContext db, string name = "restart-bgp")
    {
        var id = Guid.NewGuid();
        db.Skills.Add(new SkillModel
        {
            SkillId = id,
            Name = name,
            SkillType = "workflow",
            Triggers = new List<string> { "restart bgp" },
            Tags = new List<string>(),
            LearnedFrom = "",
            Enabled = true,
            IsActive = true,
        });
        db.SaveChanges();
        return id;
    }

    [Theory]
    [InlineData("", "workflow", "name is required")]
    [InlineData("restart-bgp", "", "skill_type is required")]
    public async Task Skill_CreateRequiresANameAndType(string name, string type, string expected)
    {
        using var db = TestDb.NewContext();

        var result = await Skills(db).PostAsync(new CreateSkill { Name = name, SkillType = type });

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(db.Skills);
    }

    [Fact]
    public async Task Skill_CreatePersistsWithTheUsageCountersZeroed()
    {
        using var db = TestDb.NewContext();

        var result = await Skills(db).PostAsync(new CreateSkill
        {
            Name = "restart-bgp",
            SkillType = "workflow",
            Triggers = new List<string> { "restart bgp", "bounce bgp" },
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(db.Skills);
        Assert.Equal(2, saved.Triggers.Count);
        Assert.Equal(0, saved.UseCount);
        Assert.Equal(0, saved.SuccessCount);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async Task Skill_UpdateMergesAndDeleteIsSoft()
    {
        using var db = TestDb.NewContext();
        var id = SeedSkill(db);

        await Skills(db).UpdateAsync(id, new UpdateSkill { Description = "restarts a BGP peer" });
        Assert.Equal("restart-bgp", db.Skills.Single().Name);
        Assert.Equal("restarts a BGP peer", db.Skills.Single().Description);

        await Skills(db).DeleteAsync(id);
        Assert.False(db.Skills.Single().IsActive);
    }

    [Fact]
    public async Task Skill_UpdatingAnUnknownIdIs404()
    {
        using var db = TestDb.NewContext();

        Assert.IsType<NotFoundObjectResult>(
            (await Skills(db).UpdateAsync(Guid.NewGuid(), new UpdateSkill { Name = "x" })).Result);
    }

    // ─── AIAgentService ─────────────────────────────────────────────────

    private static AIAgentService Agents(AppDbContext db)
        => new(new RepositoryBase<AIAgentModel>(db), Caller(), NullLogger<AIAgentService>.Instance);

    private static Guid SeedAgent(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.AIAgents.Add(new AIAgentModel
        {
            AIAgentId = id,
            Name = "netops",
            Role = "operator",
            Tools = new List<string>(),
            MaxIterations = 8,
            Temperature = 0.2,
            Enabled = true,
            IsActive = true,
        });
        db.SaveChanges();
        return id;
    }

    [Theory]
    [InlineData("", "operator", "name is required")]
    [InlineData("netops", "", "role is required")]
    public async Task Agent_CreateRequiresANameAndRole(string name, string role, string expected)
    {
        using var db = TestDb.NewContext();

        var result = await Agents(db).PostAsync(new CreateAIAgent { Name = name, Role = role });

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(db.AIAgents);
    }

    // The iteration cap and temperature have defaults because an agent
    // created without them still has to be runnable.
    [Fact]
    public async Task Agent_CreateAppliesTheRunDefaults()
    {
        using var db = TestDb.NewContext();

        await Agents(db).PostAsync(new CreateAIAgent { Name = "netops", Role = "operator" });

        var saved = Assert.Single(db.AIAgents);
        Assert.True(saved.MaxIterations > 0);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async Task Agent_ExplicitRunSettingsWin()
    {
        using var db = TestDb.NewContext();

        await Agents(db).PostAsync(new CreateAIAgent
        {
            Name = "netops", Role = "operator", MaxIterations = 3, Temperature = 0.9,
        });

        var saved = Assert.Single(db.AIAgents);
        Assert.Equal(3, saved.MaxIterations);
        Assert.Equal(0.9, saved.Temperature);
    }

    [Fact]
    public async Task Agent_UpdateMergesAndDeleteIsSoft()
    {
        using var db = TestDb.NewContext();
        var id = SeedAgent(db);

        await Agents(db).UpdateAsync(id, new UpdateAIAgent { SystemPrompt = "be terse" });
        Assert.Equal("netops", db.AIAgents.Single().Name);
        Assert.Equal("be terse", db.AIAgents.Single().SystemPrompt);

        await Agents(db).DeleteAsync(id);
        Assert.False(db.AIAgents.Single().IsActive);
    }

    // ─── AgentScratchService ────────────────────────────────────────────

    private static AgentScratchService Scratch(AppDbContext db)
        => new(new AgentScratchRepository(db), Caller(), NullLogger<AgentScratchService>.Instance);

    [Fact]
    public async Task Scratch_UpsertThenReadRoundTrips()
    {
        using var db = TestDb.NewContext();
        var conversation = Guid.NewGuid();

        await Scratch(db).UpsertAsync(conversation, "plan", TestJson.Element("""{"v":1}"""));

        var value = await Scratch(db).ReadAsync(conversation, "plan", default);
        Assert.Equal(1, value!.Value.GetProperty("v").GetInt32());
    }

    // A blank key would create an unaddressable row, so it is rejected on
    // write and answered with null on read.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Scratch_ABlankKeyIsRejectedOnWrite(string key)
    {
        using var db = TestDb.NewContext();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Scratch(db).UpsertAsync(Guid.NewGuid(), key, TestJson.Element("1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Scratch_ABlankKeyReadsAsNull(string key)
    {
        using var db = TestDb.NewContext();

        Assert.Null(await Scratch(db).ReadAsync(Guid.NewGuid(), key, default));
    }

    [Fact]
    public async Task Scratch_ReadingAnUnknownKeyIsNull()
    {
        using var db = TestDb.NewContext();

        Assert.Null(await Scratch(db).ReadAsync(Guid.NewGuid(), "nope", default));
    }

    [Fact]
    public async Task Scratch_ListReturnsTheConversationsEntries()
    {
        using var db = TestDb.NewContext();
        var conversation = Guid.NewGuid();
        await Scratch(db).UpsertAsync(conversation, "plan", TestJson.Element("1"), note: "the plan");
        await Scratch(db).UpsertAsync(conversation, "notes", TestJson.Element("2"));
        await Scratch(db).UpsertAsync(Guid.NewGuid(), "other", TestJson.Element("3"));

        var entries = await Scratch(db).ListAsync(conversation, default);

        Assert.Equal(2, entries.Count);
        Assert.Equal("the plan", entries.Single(e => e.Key == "plan").Note);
    }

    // ─── AllowedPythonModuleService ─────────────────────────────────────

    private static AllowedPythonModuleService Modules(AppDbContext db)
        => new(new AllowedPythonModuleRepository(db), Caller(), new FakeAudit());

    // Nothing but a bare top-level identifier is ever interpolated into generated
    // python, so anything carrying punctuation that could change the meaning of that
    // script is refused outright, whatever the source.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("import os; os.system('rm -rf /')")]
    [InlineData("os, sys")]
    [InlineData("os as x")]
    public async Task PythonModules_AnInvalidImportNameIsRejected(string importName)
    {
        using var db = TestDb.NewContext();

        await Assert.ThrowsAsync<ValidationException>(() => Modules(db).CreateAsync(
            new CreateAllowedPythonModule { ImportName = importName }));

        Assert.Empty(db.AllowedPythonModules);
    }

    // A pip row may be named the way pip names it — python-dateutil, ruamel.yaml —
    // because the import name is read off the installed distribution afterwards.
    // Neither is a valid identifier and neither ever reaches the generated script:
    // the provisioner fails the row if discovery cannot resolve one.
    [Theory]
    [InlineData("python-dateutil")]
    [InlineData("ruamel.yaml")]
    [InlineData("beautifulsoup4")]
    public async Task PythonModules_APipRowAcceptsAPackageName(string importName)
    {
        using var db = TestDb.NewContext();

        await Modules(db).CreateAsync(new CreateAllowedPythonModule
        {
            ImportName = importName,
            Source = AllowedPythonModule.SourcePip,
        });

        // Stored as typed. The provisioner replaces it with the name the installed
        // distribution reports, and the row stays `pending` until it does.
        var row = Assert.Single(db.AllowedPythonModules);
        Assert.Equal(importName, row.ImportName);
        Assert.Equal(AllowedPythonModule.StatusPending, row.Status);
    }

    // A stdlib row has no installer to ask, so its name has to be the import name.
    [Theory]
    [InlineData("os.path")]
    [InlineData("net-miko")]
    public async Task PythonModules_AStdlibRowStillRequiresAnImportName(string importName)
    {
        using var db = TestDb.NewContext();

        await Assert.ThrowsAsync<ValidationException>(() => Modules(db).CreateAsync(
            new CreateAllowedPythonModule
            {
                ImportName = importName,
                Source = AllowedPythonModule.SourceStdlib,
            }));

        Assert.Empty(db.AllowedPythonModules);
    }

    [Fact]
    public async Task PythonModules_AnUnknownSourceIsRejected()
    {
        using var db = TestDb.NewContext();

        await Assert.ThrowsAsync<ValidationException>(() => Modules(db).CreateAsync(
            new CreateAllowedPythonModule { ImportName = "netmiko", Source = "conda" }));
    }

    // The pip spec becomes a shell argument for the provisioner, so its
    // character set is constrained too.
    [Fact]
    public async Task PythonModules_AnInvalidPipSpecIsRejected()
    {
        using var db = TestDb.NewContext();

        await Assert.ThrowsAsync<ValidationException>(() => Modules(db).CreateAsync(
            new CreateAllowedPythonModule
            {
                ImportName = "netmiko", Source = "pip", PipSpec = "netmiko; rm -rf /",
            }));
    }

    // stdlib needs no install, so it is usable at once; pip has to wait for
    // the provisioner to actually install it.
    [Fact]
    public async Task PythonModules_StdlibIsReadyImmediately()
    {
        using var db = TestDb.NewContext();

        await Modules(db).CreateAsync(new CreateAllowedPythonModule
        {
            ImportName = "json", Source = "stdlib",
        });

        var saved = Assert.Single(db.AllowedPythonModules);
        Assert.Equal(AllowedPythonModule.StatusReady, saved.Status);
        Assert.Null(saved.PipSpec);
    }

    [Fact]
    public async Task PythonModules_APipModuleWaitsForTheProvisioner()
    {
        using var db = TestDb.NewContext();

        await Modules(db).CreateAsync(new CreateAllowedPythonModule
        {
            ImportName = "netmiko", Source = "pip",
        });

        var saved = Assert.Single(db.AllowedPythonModules);
        Assert.Equal(AllowedPythonModule.StatusPending, saved.Status);
        // With no explicit spec the import name doubles as the pip spec.
        Assert.Equal("netmiko", saved.PipSpec);
        Assert.Equal(User, saved.CreatedBy);
    }

    [Fact]
    public async Task PythonModules_ADuplicateImportNameIsRejected()
    {
        using var db = TestDb.NewContext();
        await Modules(db).CreateAsync(new CreateAllowedPythonModule { ImportName = "netmiko" });

        await Assert.ThrowsAsync<ValidationException>(() => Modules(db).CreateAsync(
            new CreateAllowedPythonModule { ImportName = "netmiko" }));

        Assert.Single(db.AllowedPythonModules);
    }

    // HARD delete on purpose: the unique (ImportName) index counts
    // inactive rows, so a soft delete would make the name unaddable forever.
    [Fact]
    public async Task PythonModules_DeleteIsHardSoTheNameCanBeReAdded()
    {
        using var db = TestDb.NewContext();
        var created = await Modules(db).CreateAsync(new CreateAllowedPythonModule { ImportName = "netmiko" });
        var id = created.Value!.AllowedPythonModuleId;

        await Modules(db).DeleteAsync(id);
        Assert.Empty(db.AllowedPythonModules);

        await Modules(db).CreateAsync(new CreateAllowedPythonModule { ImportName = "netmiko" });
        Assert.Single(db.AllowedPythonModules);
    }

    [Fact]
    public async Task PythonModules_DeletingAnUnknownIdIsNotFound()
    {
        using var db = TestDb.NewContext();

        await Assert.ThrowsAsync<NotFoundException>(() => Modules(db).DeleteAsync(Guid.NewGuid()));
    }

    // Retry re-queues a failed install and clears the old error, so the
    // admin sees a clean "installing" rather than a stale failure.
    [Fact]
    public async Task PythonModules_RetryRequeuesAFailedPipInstall()
    {
        using var db = TestDb.NewContext();
        var created = await Modules(db).CreateAsync(new CreateAllowedPythonModule { ImportName = "netmiko" });
        var id = created.Value!.AllowedPythonModuleId;
        var row = db.AllowedPythonModules.Single();
        row.Status = AllowedPythonModule.StatusFailed;
        row.Error = "no matching distribution";
        db.SaveChanges();

        await Modules(db).RetryAsync(id);

        var saved = db.AllowedPythonModules.Single();
        Assert.Equal(AllowedPythonModule.StatusPending, saved.Status);
        Assert.Null(saved.Error);
    }

    // A stdlib module has no install to retry, so it is left as-is.
    [Fact]
    public async Task PythonModules_RetryOnStdlibIsANoOp()
    {
        using var db = TestDb.NewContext();
        var created = await Modules(db).CreateAsync(
            new CreateAllowedPythonModule { ImportName = "json", Source = "stdlib" });

        await Modules(db).RetryAsync(created.Value!.AllowedPythonModuleId);

        Assert.Equal(AllowedPythonModule.StatusReady, db.AllowedPythonModules.Single().Status);
    }

    // ─── DeviceService ──────────────────────────────────────────────────

    private static DeviceService Devices(AppDbContext db)
        => new(new DeviceRepository(db), Caller(), new FakeAudit(), NullLogger<DeviceService>.Instance);

    [Fact]
    public async Task Device_CreateRequiresAName()
    {
        using var db = TestDb.NewContext();

        var result = await Devices(db).PostAsync(new CreateDevice { DeviceName = "" });

        Assert.Equal("device_name is required", ErrorOf(result));
        Assert.Empty(db.Devices);
    }

    // The environment trio decides which runs may target the device; the
    // defaults deliberately exclude qa so a lab flag is always explicit.
    [Fact]
    public async Task Device_CreateAppliesTheEnvironmentDefaults()
    {
        using var db = TestDb.NewContext();

        await Devices(db).PostAsync(new CreateDevice { DeviceName = "r1" });

        var saved = Assert.Single(db.Devices);
        Assert.True(saved.AllowDraft);
        Assert.False(saved.AllowQa);
        Assert.True(saved.AllowProduction);
    }

    [Fact]
    public async Task Device_ExplicitEnvironmentFlagsWin()
    {
        using var db = TestDb.NewContext();

        await Devices(db).PostAsync(new CreateDevice
        {
            DeviceName = "lab-r1", AllowDraft = false, AllowQa = true, AllowProduction = false,
        });

        var saved = Assert.Single(db.Devices);
        Assert.False(saved.AllowDraft);
        Assert.True(saved.AllowQa);
        Assert.False(saved.AllowProduction);
    }

    // A blank external id becomes null so the "already imported from NetBox"
    // lookup doesn't match every hand-created device.
    [Fact]
    public async Task Device_ABlankExternalIdBecomesNull()
    {
        using var db = TestDb.NewContext();

        await Devices(db).PostAsync(new CreateDevice { DeviceName = "r1", ExternalId = "   " });

        Assert.Null(db.Devices.Single().ExternalId);
    }

    [Fact]
    public async Task Device_UpdateMergesAndDeleteIsSoft()
    {
        using var db = TestDb.NewContext();
        db.Devices.Add(new DeviceModel
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = "r1",
            IpAddress = "10.0.0.1",
            IsActive = true,
        });
        db.SaveChanges();
        var id = db.Devices.Single().DeviceId;

        await Devices(db).UpdateAsync(id, new UpdateDevice { Site = "madrid" });
        Assert.Equal("r1", db.Devices.Single().DeviceName);
        Assert.Equal("madrid", db.Devices.Single().Site);

        await Devices(db).DeleteAsync(id);
        Assert.False(db.Devices.Single().IsActive);
    }

    [Fact]
    public async Task Device_AnUnknownIdIs404()
    {
        using var db = TestDb.NewContext();

        Assert.IsType<NotFoundObjectResult>((await Devices(db).GetByIdAsync(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundObjectResult>((await Devices(db).DeleteAsync(Guid.NewGuid())).Result);
    }
}

