using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonEnvironments;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Device;
using flow_weaver_backend.Services.PythonEnvironments;
using flow_weaver_backend.Services.Snippet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Admin-defined uv Python environments: validation, masking, the
// single-fallback rule, status lifecycle, the resolver's refusals, the SSH
// runner's fallback decision and the device / snippet references.
public class PythonEnvironmentTests
{
    private const string Requirements = "netmiko>=4,<5\nparamiko>=3.5,<5\n";

    private sealed class RecordingAudit : flow_weaver_backend.Services.Audit.IAuditLogger
    {
        public List<(string Action, string Payload)> Entries { get; } = new();
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Entries.Add((action, JsonSerializer.Serialize(new { before, after })));
            return Task.CompletedTask;
        }
    }

    private static (PythonEnvironmentService Service, AppDbContext Db, RecordingAudit Audit) Build()
    {
        var db = TestDb.NewContext();
        var audit = new RecordingAudit();
        return (new PythonEnvironmentService(new PythonEnvironmentRepository(db), new FakeUser(), audit), db, audit);
    }

    private static PythonEnvironmentResponse Created(ActionResult<PythonEnvironmentResponse> r) =>
        (PythonEnvironmentResponse)Assert.IsType<CreatedAtActionResult>(r.Result).Value!;

    private static async Task<PythonEnvironmentResponse> CreateAsync(
        PythonEnvironmentService svc, string name, bool fallback = false, string requirements = Requirements)
        => Created(await svc.CreateAsync(new CreatePythonEnvironment
        {
            Name = name,
            Requirements = requirements,
            IsSshFallback = fallback,
        }));

    // ─── name validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("ssh-legacy", true)]
    [InlineData("abc", true)]
    [InlineData("a1b", true)]
    [InlineData("py311-netmiko4", true)]
    [InlineData("ab", false)]            // too short
    [InlineData("-abc", false)]          // must start alnum
    [InlineData("abc-", false)]          // must end alnum
    [InlineData("Abc", false)]           // lowercase only
    [InlineData("ab_c", false)]
    [InlineData("ab.c", false)]          // '.' would collide with build dirs
    [InlineData("ab c", false)]
    [InlineData("../etc", false)]
    [InlineData("system", false)]        // reserved: the no-environment label
    [InlineData("", false)]
    public void Name_validation(string name, bool valid)
        => Assert.Equal(valid, PythonEnvironmentText.IsValidName(name));

    [Fact]
    public void Name_validation_caps_length_at_50()
    {
        Assert.True(PythonEnvironmentText.IsValidName(new string('a', 50)));
        Assert.False(PythonEnvironmentText.IsValidName(new string('a', 51)));
    }

    [Fact]
    public async Task Create_rejects_an_invalid_or_duplicate_name()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var bad = await Assert.ThrowsAsync<ValidationException>(() => svc.CreateAsync(
            new CreatePythonEnvironment { Name = "Bad_Name", Requirements = Requirements }));
        Assert.Equal("name_invalid", bad.Code);

        await CreateAsync(svc, "ssh-legacy");
        var dup = await Assert.ThrowsAsync<ValidationException>(() => svc.CreateAsync(
            new CreatePythonEnvironment { Name = "ssh-legacy", Requirements = Requirements }));
        Assert.Equal("name_duplicate", dup.Code);
    }

    [Fact]
    public async Task A_deleted_environment_frees_its_name()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var env = await CreateAsync(svc, "ssh-legacy");
        Assert.IsType<NoContentResult>(await svc.DeleteAsync(env.PythonEnvironmentId));

        var again = await CreateAsync(svc, "ssh-legacy");
        Assert.NotEqual(env.PythonEnvironmentId, again.PythonEnvironmentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n# only a comment\n")]
    [InlineData("-r other.txt\nnetmiko")]
    [InlineData("--constraint c.txt\nnetmiko")]
    [InlineData("--index-url https://pypi.org/simple")]
    public void Requirements_validation_rejects(string requirements)
        => Assert.NotNull(PythonEnvironmentText.ValidateRequirements(requirements));

    [Fact]
    public void Requirements_validation_accepts_options_with_packages()
        => Assert.Null(PythonEnvironmentText.ValidateRequirements(
            "--index-url https://u:p@pkgs.example.com/simple\n# pinned\nparamiko>=3.5,<5\n"));

    // ─── single SSH fallback ────────────────────────────────────────────

    [Fact]
    public async Task Only_one_active_environment_can_be_the_ssh_fallback()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var first = await CreateAsync(svc, "ssh-legacy", fallback: true);

        var onCreate = await Assert.ThrowsAsync<ValidationException>(() => svc.CreateAsync(
            new CreatePythonEnvironment { Name = "other", Requirements = Requirements, IsSshFallback = true }));
        Assert.Equal("ssh_fallback_taken", onCreate.Code);

        var other = await CreateAsync(svc, "other");
        var onUpdate = await Assert.ThrowsAsync<ValidationException>(() => svc.UpdateAsync(
            other.PythonEnvironmentId, new UpdatePythonEnvironment { IsSshFallback = true }));
        Assert.Equal("ssh_fallback_taken", onUpdate.Code);

        // Re-saving the current fallback as fallback is not a conflict with itself.
        var same = (await svc.UpdateAsync(first.PythonEnvironmentId,
            new UpdatePythonEnvironment { IsSshFallback = true })).Value!;
        Assert.True(same.IsSshFallback);

        // Unset it there, then the other one may take it.
        await svc.UpdateAsync(first.PythonEnvironmentId, new UpdatePythonEnvironment { IsSshFallback = false });
        var moved = (await svc.UpdateAsync(other.PythonEnvironmentId,
            new UpdatePythonEnvironment { IsSshFallback = true })).Value!;
        Assert.True(moved.IsSshFallback);
    }

    [Fact]
    public async Task Deleting_the_fallback_releases_the_flag()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var first = await CreateAsync(svc, "ssh-legacy", fallback: true);
        await svc.DeleteAsync(first.PythonEnvironmentId);

        var second = await CreateAsync(svc, "ssh-legacy-2", fallback: true);
        Assert.True(second.IsSshFallback);
    }

    // ─── credential masking ─────────────────────────────────────────────

    [Fact]
    public void Masking_hides_url_userinfo_and_tokens()
    {
        var text = "--extra-index-url https://bob:s3cret@pkgs.example.com/simple\n"
            + "pkg @ git+https://ghp_TOKEN@github.com/o/r.git\n"
            + "paramiko>=3.5,<5\n";

        var masked = PythonEnvironmentText.MaskCredentials(text)!;

        Assert.DoesNotContain("s3cret", masked);
        Assert.DoesNotContain("bob", masked);
        Assert.DoesNotContain("ghp_TOKEN", masked);
        Assert.Contains("https://***@pkgs.example.com/simple", masked);
        Assert.Contains("git+https://***@github.com/o/r.git", masked);
        Assert.Contains("paramiko>=3.5,<5", masked);
    }

    [Fact]
    public async Task Responses_and_audit_never_carry_index_credentials()
    {
        var (svc, db, audit) = Build();
        using var _db = db;

        var created = await CreateAsync(svc, "private-index",
            requirements: "--index-url https://bob:s3cret@pkgs.example.com/simple\nparamiko>=3.5,<5\n");

        Assert.DoesNotContain("s3cret", created.Requirements);
        Assert.Contains("***@pkgs.example.com", created.Requirements);
        Assert.All(audit.Entries, e => Assert.DoesNotContain("s3cret", e.Payload));

        // The stored row keeps the real credentials — the builder needs them.
        var row = db.PythonEnvironments.Single();
        Assert.Contains("bob:s3cret@", row.Requirements);

        var listed = (ListResponse<PythonEnvironmentResponse>)
            Assert.IsType<OkObjectResult>((await svc.ListAsync(50, 0)).Result).Value!;
        Assert.DoesNotContain("s3cret", listed.Data.Single().Requirements);
    }

    [Fact]
    public async Task An_edit_form_can_round_trip_the_masked_requirements()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var created = await CreateAsync(svc, "private-index",
            requirements: "--index-url https://bob:s3cret@pkgs.example.com/simple\nparamiko>=3.5,<5\n");

        // The form sends back what it was shown, plus one new package.
        var edited = created.Requirements.TrimEnd('\n') + "\ntextfsm>=1.1\n";
        await svc.UpdateAsync(created.PythonEnvironmentId, new UpdatePythonEnvironment { Requirements = edited });

        var row = db.PythonEnvironments.Single();
        Assert.Contains("https://bob:s3cret@pkgs.example.com/simple", row.Requirements);
        Assert.Contains("textfsm>=1.1", row.Requirements);
        Assert.DoesNotContain("***", row.Requirements);
    }

    [Fact]
    public async Task A_masked_line_that_was_edited_cannot_be_saved()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var created = await CreateAsync(svc, "private-index",
            requirements: "--index-url https://bob:s3cret@pkgs.example.com/simple\nparamiko>=3.5,<5\n");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => svc.UpdateAsync(
            created.PythonEnvironmentId,
            new UpdatePythonEnvironment { Requirements = "--index-url https://***@other.example.com/simple\nparamiko\n" }));
        Assert.Equal("requirements_masked", ex.Code);
        Assert.Contains("bob:s3cret@", db.PythonEnvironments.Single().Requirements);
    }

    [Fact]
    public async Task Create_refuses_a_literal_mask()
    {
        var (svc, db, _) = Build();
        using var _db = db;

        var ex = await Assert.ThrowsAsync<ValidationException>(() => svc.CreateAsync(new CreatePythonEnvironment
        {
            Name = "copy-paste",
            Requirements = "--index-url https://***@pkgs.example.com/simple\nparamiko\n",
        }));
        Assert.Equal("requirements_masked", ex.Code);
    }

    // ─── status lifecycle ───────────────────────────────────────────────

    private static void MarkBuilt(AppDbContext db, Guid id, string? error = null)
    {
        var row = db.PythonEnvironments.Single(e => e.PythonEnvironmentId == id);
        row.Status = error is null ? PythonEnvironment.StatusReady : PythonEnvironment.StatusFailed;
        row.LastError = error;
        row.ResolvedPackages = error is null ? "paramiko==4.0.0" : null;
        db.SaveChanges();
    }

    [Fact]
    public async Task New_environments_start_pending()
    {
        var (svc, db, _) = Build();
        using var _db = db;
        Assert.Equal(PythonEnvironment.StatusPending, (await CreateAsync(svc, "fresh")).Status);
    }

    [Fact]
    public async Task Editing_requirements_or_python_version_resets_status_to_pending()
    {
        var (svc, db, _) = Build();
        using var _db = db;
        var env = await CreateAsync(svc, "ssh-legacy");

        MarkBuilt(db, env.PythonEnvironmentId, error: "boom");
        var afterReq = (await svc.UpdateAsync(env.PythonEnvironmentId,
            new UpdatePythonEnvironment { Requirements = "paramiko>=3.5,<5\n" })).Value!;
        Assert.Equal(PythonEnvironment.StatusPending, afterReq.Status);
        Assert.Null(afterReq.LastError);

        MarkBuilt(db, env.PythonEnvironmentId);
        var afterVersion = (await svc.UpdateAsync(env.PythonEnvironmentId,
            new UpdatePythonEnvironment { PythonVersion = "3.12" })).Value!;
        Assert.Equal(PythonEnvironment.StatusPending, afterVersion.Status);
        Assert.Equal("3.12", afterVersion.PythonVersion);

        // "" clears the version back to the image's python3 — also a rebuild.
        MarkBuilt(db, env.PythonEnvironmentId);
        var cleared = (await svc.UpdateAsync(env.PythonEnvironmentId,
            new UpdatePythonEnvironment { PythonVersion = "" })).Value!;
        Assert.Null(cleared.PythonVersion);
        Assert.Equal(PythonEnvironment.StatusPending, cleared.Status);
    }

    [Fact]
    public async Task Editing_only_the_description_or_fallback_flag_keeps_the_build()
    {
        var (svc, db, _) = Build();
        using var _db = db;
        var env = await CreateAsync(svc, "ssh-legacy");
        MarkBuilt(db, env.PythonEnvironmentId);

        var updated = (await svc.UpdateAsync(env.PythonEnvironmentId, new UpdatePythonEnvironment
        {
            Description = "why",
            IsSshFallback = true,
            // Same requirements, re-sent as the form would.
            Requirements = Requirements,
        })).Value!;

        Assert.Equal(PythonEnvironment.StatusReady, updated.Status);
        Assert.Equal("why", updated.Description);
    }

    [Fact]
    public async Task Rebuild_resets_status_to_pending()
    {
        var (svc, db, audit) = Build();
        using var _db = db;
        var env = await CreateAsync(svc, "ssh-legacy");
        MarkBuilt(db, env.PythonEnvironmentId, error: "no wheel");

        var rebuilt = (await svc.RebuildAsync(env.PythonEnvironmentId)).Value!;

        Assert.Equal(PythonEnvironment.StatusPending, rebuilt.Status);
        Assert.Null(rebuilt.LastError);
        Assert.Contains(audit.Entries, e => e.Action == "rebuild");
    }

    [Fact]
    public async Task Delete_refuses_an_environment_still_in_use()
    {
        var (svc, db, _) = Build();
        using var _db = db;
        var env = await CreateAsync(svc, "ssh-legacy");
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = "exos-1",
            PythonEnvironmentId = env.PythonEnvironmentId,
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => svc.DeleteAsync(env.PythonEnvironmentId));
        Assert.Contains("1 device", ex.Message);
    }

    // ─── resolver ───────────────────────────────────────────────────────

    private sealed class TempEnvsRoot : IDisposable
    {
        public string PackagesDir { get; } = Path.Combine(Path.GetTempPath(), "fw-pyenv-test-" + Guid.NewGuid().ToString("N"));
        public string EnvsRoot => Path.Combine(PackagesDir, PythonEnvironmentLayout.EnvsDirName);

        public IConfiguration Config => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Python:PackagesDir"] = PackagesDir })
            .Build();

        public void CreateEnvOnDisk(string name)
        {
            var bin = Path.Combine(EnvsRoot, name, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "python"), string.Empty);
        }

        public void Dispose()
        {
            try { Directory.Delete(PackagesDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static PythonEnvironment Row(string name, string status, bool active = true, bool fallback = false) => new()
    {
        PythonEnvironmentId = Guid.NewGuid(),
        Name = name,
        Requirements = Requirements,
        Status = status,
        IsActive = active,
        IsSshFallback = fallback,
    };

    [Fact]
    public async Task Resolver_returns_the_interpreter_of_a_ready_environment()
    {
        using var root = new TempEnvsRoot();
        using var db = TestDb.NewContext();
        var row = Row("ssh-legacy", PythonEnvironment.StatusReady);
        db.PythonEnvironments.Add(row);
        await db.SaveChangesAsync();
        root.CreateEnvOnDisk("ssh-legacy");

        var resolved = await new PythonEnvironmentResolver(new PythonEnvironmentRepository(db), root.Config)
            .ResolveAsync(row.PythonEnvironmentId);

        Assert.Equal("ssh-legacy", resolved.Name);
        Assert.Equal(Path.Combine(root.EnvsRoot, "ssh-legacy", "bin", "python"), resolved.Interpreter);
        Assert.Equal(Path.Combine(root.EnvsRoot, "ssh-legacy", "bin"), resolved.BinDir);
    }

    [Fact]
    public async Task Resolver_refuses_an_unknown_environment()
    {
        using var root = new TempEnvsRoot();
        using var db = TestDb.NewContext();

        var ex = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            new PythonEnvironmentResolver(new PythonEnvironmentRepository(db), root.Config)
                .ResolveAsync(Guid.NewGuid()));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task Resolver_refuses_a_deleted_environment()
    {
        using var root = new TempEnvsRoot();
        using var db = TestDb.NewContext();
        var row = Row("gone", PythonEnvironment.StatusReady, active: false);
        db.PythonEnvironments.Add(row);
        await db.SaveChangesAsync();
        root.CreateEnvOnDisk("gone");

        var ex = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            new PythonEnvironmentResolver(new PythonEnvironmentRepository(db), root.Config)
                .ResolveAsync(row.PythonEnvironmentId));
        Assert.Contains("deleted", ex.Message);
    }

    [Theory]
    [InlineData(PythonEnvironment.StatusPending)]
    [InlineData(PythonEnvironment.StatusBuilding)]
    [InlineData(PythonEnvironment.StatusFailed)]
    public async Task Resolver_refuses_an_environment_that_is_not_ready(string status)
    {
        using var root = new TempEnvsRoot();
        using var db = TestDb.NewContext();
        var row = Row("ssh-legacy", status);
        row.LastError = status == PythonEnvironment.StatusFailed
            ? "uv pip install failed: https://bob:s3cret@pkgs.example.com"
            : null;
        db.PythonEnvironments.Add(row);
        await db.SaveChangesAsync();
        // A previous good build on disk does not make a non-ready row usable.
        root.CreateEnvOnDisk("ssh-legacy");

        var ex = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            new PythonEnvironmentResolver(new PythonEnvironmentRepository(db), root.Config)
                .ResolveAsync(row.PythonEnvironmentId));
        Assert.Contains("not ready", ex.Message);
        Assert.Contains(status, ex.Message);
        Assert.DoesNotContain("s3cret", ex.Message);
    }

    [Fact]
    public async Task Resolver_refuses_a_ready_environment_whose_directory_is_missing()
    {
        using var root = new TempEnvsRoot();
        using var db = TestDb.NewContext();
        var row = Row("ssh-legacy", PythonEnvironment.StatusReady);
        db.PythonEnvironments.Add(row);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            new PythonEnvironmentResolver(new PythonEnvironmentRepository(db), root.Config)
                .ResolveAsync(row.PythonEnvironmentId));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task Resolver_finds_the_ready_ssh_fallback_unless_it_is_excluded()
    {
        using var root = new TempEnvsRoot();
        using var db = TestDb.NewContext();
        var fallback = Row("ssh-legacy", PythonEnvironment.StatusReady, fallback: true);
        db.PythonEnvironments.Add(fallback);
        db.PythonEnvironments.Add(Row("other", PythonEnvironment.StatusReady));
        await db.SaveChangesAsync();
        root.CreateEnvOnDisk("ssh-legacy");

        var resolver = new PythonEnvironmentResolver(new PythonEnvironmentRepository(db), root.Config);
        Assert.Equal("ssh-legacy", (await resolver.ResolveSshFallbackAsync(null))?.Name);
        Assert.Null(await resolver.ResolveSshFallbackAsync(fallback.PythonEnvironmentId));

        // Not ready → no fallback (never a half-built one).
        fallback.Status = PythonEnvironment.StatusBuilding;
        await db.SaveChangesAsync();
        Assert.Null(await resolver.ResolveSshFallbackAsync(null));
    }

    // ─── SSH fallback decision ──────────────────────────────────────────

    private const string IncompatiblePeer =
        "connect failed: Incompatible ssh peer (no acceptable host key)";

    [Theory]
    [InlineData(IncompatiblePeer, true)]
    [InlineData("connect failed: no acceptable host key", true)]
    [InlineData("INCOMPATIBLE SSH PEER", true)]
    [InlineData("authentication failed: bad password", false)]
    [InlineData("host key mismatch for 10.0.0.1: expected SHA256:a, got SHA256:b", false)]
    [InlineData("connection timeout: timed out", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Fallback_recognises_only_the_host_key_negotiation_failure(string? error, bool expected)
        => Assert.Equal(expected, SshEnvironmentFallback.IsHostKeyIncompatibility(error));

    [Fact]
    public void Fallback_retries_from_system_python_with_the_fallback_env()
        => Assert.True(SshEnvironmentFallback.ShouldRetry(IncompatiblePeer, usedEnvironmentId: null, Guid.NewGuid()));

    [Fact]
    public void Fallback_retries_from_another_env_with_the_fallback_env()
        => Assert.True(SshEnvironmentFallback.ShouldRetry(IncompatiblePeer, Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void Fallback_does_not_retry_with_the_env_that_just_failed()
    {
        var id = Guid.NewGuid();
        Assert.False(SshEnvironmentFallback.ShouldRetry(IncompatiblePeer, id, id));
    }

    [Fact]
    public void Fallback_does_not_retry_without_a_ready_fallback_env()
        => Assert.False(SshEnvironmentFallback.ShouldRetry(IncompatiblePeer, null, fallbackEnvironmentId: null));

    [Fact]
    public void Fallback_does_not_retry_other_errors()
        => Assert.False(SshEnvironmentFallback.ShouldRetry("authentication failed", null, Guid.NewGuid()));

    // ─── device / snippet references ────────────────────────────────────

    private static async Task<Guid> SeedEnvAsync(AppDbContext db)
    {
        var row = Row("ssh-legacy", PythonEnvironment.StatusReady);
        db.PythonEnvironments.Add(row);
        await db.SaveChangesAsync();
        return row.PythonEnvironmentId;
    }

    private static DeviceService Devices(AppDbContext db) => new(
        new DeviceRepository(db), new FakeUser(), new FakeAudit(), NullLogger<DeviceService>.Instance,
        new PythonEnvironmentRepository(db));

    [Fact]
    public async Task Device_python_environment_id_round_trips_and_clears()
    {
        using var db = TestDb.NewContext();
        var envId = await SeedEnvAsync(db);
        var svc = Devices(db);

        var created = (DeviceResponse)Assert.IsType<CreatedAtActionResult>((await svc.PostAsync(
            new CreateDevice { DeviceName = "exos-1", PythonEnvironmentId = envId })).Result).Value!;
        Assert.Equal(envId, created.PythonEnvironmentId);
        Assert.Equal(envId, (await svc.GetByIdAsync(created.DeviceId)).Value!.PythonEnvironmentId);

        // Omitted on update keeps it.
        var kept = (await svc.UpdateAsync(created.DeviceId, new UpdateDevice { Site = "lab" })).Value!;
        Assert.Equal(envId, kept.PythonEnvironmentId);

        // The empty GUID clears it back to system python3.
        var cleared = (await svc.UpdateAsync(created.DeviceId,
            new UpdateDevice { PythonEnvironmentId = Guid.Empty })).Value!;
        Assert.Null(cleared.PythonEnvironmentId);
        Assert.Null(db.Devices.Single().PythonEnvironmentId);
    }

    [Fact]
    public async Task Device_rejects_an_unknown_python_environment()
    {
        using var db = TestDb.NewContext();
        var result = await Devices(db).PostAsync(
            new CreateDevice { DeviceName = "exos-1", PythonEnvironmentId = Guid.NewGuid() });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Device_without_environment_stays_on_system_python()
    {
        using var db = TestDb.NewContext();
        var created = (DeviceResponse)Assert.IsType<CreatedAtActionResult>(
            (await Devices(db).PostAsync(new CreateDevice { DeviceName = "ios-1" })).Result).Value!;
        Assert.Null(created.PythonEnvironmentId);
    }

    private static SnippetService Snippets(AppDbContext db) => new(
        new SnippetRepository(db), new StepRunRepository(db), new FakeUser(), new FakeAudit(), new FakeTrace(),
        NullLogger<SnippetService>.Instance, new PythonEnvironmentRepository(db));

    private static CreateSnippet PythonSnippet(Guid? envId, string type = "python_snippet") => new()
    {
        Name = "collect",
        Type = type,
        TargetMode = "once",
        Code = "print(1)",
        LogicDiagramMermaid = "graph TD\n  a --> b",
        PythonEnvironmentId = envId,
    };

    [Fact]
    public async Task Snippet_python_environment_id_round_trips_and_clears()
    {
        using var db = TestDb.NewContext();
        var envId = await SeedEnvAsync(db);
        var svc = Snippets(db);

        var created = (SnippetResponse)Assert.IsType<CreatedAtActionResult>(
            (await svc.PostAsync(PythonSnippet(envId))).Result).Value!;
        Assert.Equal(envId, created.PythonEnvironmentId);
        Assert.Equal(envId, (await svc.GetByIdAsync(created.SnippetId)).Value!.PythonEnvironmentId);

        var kept = (await svc.UpdateAsync(created.SnippetId, new UpdateSnippet { Description = "d" })).Value!;
        Assert.Equal(envId, kept.PythonEnvironmentId);

        var cleared = (await svc.UpdateAsync(created.SnippetId,
            new UpdateSnippet { PythonEnvironmentId = Guid.Empty })).Value!;
        Assert.Null(cleared.PythonEnvironmentId);
    }

    [Fact]
    public async Task Ansible_snippets_may_use_an_environment()
    {
        using var db = TestDb.NewContext();
        var envId = await SeedEnvAsync(db);

        var created = (SnippetResponse)Assert.IsType<CreatedAtActionResult>(
            (await Snippets(db).PostAsync(PythonSnippet(envId, "ansible_playbook"))).Result).Value!;
        Assert.Equal(envId, created.PythonEnvironmentId);
    }

    [Fact]
    public async Task Snippet_rejects_an_environment_on_a_non_python_type_or_an_unknown_id()
    {
        using var db = TestDb.NewContext();
        var envId = await SeedEnvAsync(db);
        var svc = Snippets(db);

        Assert.IsType<BadRequestObjectResult>((await svc.PostAsync(PythonSnippet(envId, "ping"))).Result);
        Assert.IsType<BadRequestObjectResult>((await svc.PostAsync(PythonSnippet(Guid.NewGuid()))).Result);
    }

    // ─── seed ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Seed_creates_ssh_legacy_as_the_fallback_on_an_empty_table()
    {
        using var db = TestDb.NewContext();
        await PythonEnvironmentSeeder.SeedAsync(TestScopes.Over(db), NullLogger.Instance);

        var env = db.PythonEnvironments.Single();
        Assert.Equal("ssh-legacy", env.Name);
        Assert.True(env.IsSshFallback);
        Assert.Equal(PythonEnvironment.StatusPending, env.Status);
        Assert.Contains("paramiko>=3.5,<5", env.Requirements);
        Assert.Contains("netmiko>=4,<5", env.Requirements);
        Assert.Contains("ssh-rsa", env.Description);
        Assert.Null(PythonEnvironmentText.ValidateRequirements(env.Requirements));
    }

    [Fact]
    public async Task Seed_leaves_an_admin_curated_table_alone()
    {
        using var db = TestDb.NewContext();
        db.PythonEnvironments.Add(Row("ssh-legacy", PythonEnvironment.StatusReady, active: false));
        await db.SaveChangesAsync();

        await PythonEnvironmentSeeder.SeedAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Single(db.PythonEnvironments);
        Assert.False(db.PythonEnvironments.Single().IsActive);
    }
}
