using System.Text.Json;
using flow_weaver_backend.BackgroundServices;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonEnvironments;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.PythonEnvironments;
using flow_weaver_backend.Services.PythonModules;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// Install targets of the Packages allowlist: "System Python" (InstallInSystem)
// plus zero or more uv environments (python_module_environments). An
// environment gets a row's spec through its EFFECTIVE requirements, so a
// target change marks exactly the affected environments `pending`.
public class PythonModuleInstallTargetsTests
{
    private const string EnvRequirements = "netmiko>=4,<5\nparamiko>=3.5,<5\n";

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

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingAudit Audit { get; } = new();
        public AllowedPythonModuleService Modules { get; }
        public PythonEnvironmentService Environments { get; }
        public PythonEnvironmentRepository EnvRepo { get; }

        public Fixture()
        {
            EnvRepo = new PythonEnvironmentRepository(Db);
            Modules = new AllowedPythonModuleService(new AllowedPythonModuleRepository(Db), new FakeUser(), Audit);
            Environments = new PythonEnvironmentService(EnvRepo, new FakeUser(), Audit);
        }

        // A built environment: `ready`, so a later `pending` is observable.
        public async Task<Guid> ReadyEnvAsync(string name, string requirements = EnvRequirements)
        {
            var created = (PythonEnvironmentResponse)Assert.IsType<CreatedAtActionResult>(
                (await Environments.CreateAsync(new CreatePythonEnvironment
                {
                    Name = name,
                    Requirements = requirements,
                })).Result).Value!;
            var env = Db.PythonEnvironments.Single(e => e.PythonEnvironmentId == created.PythonEnvironmentId);
            env.Status = PythonEnvironment.StatusReady;
            await Db.SaveChangesAsync();
            return env.PythonEnvironmentId;
        }

        public string Status(Guid envId) => Db.PythonEnvironments.Single(e => e.PythonEnvironmentId == envId).Status;

        public async Task MarkAllReadyAsync()
        {
            foreach (var e in Db.PythonEnvironments) e.Status = PythonEnvironment.StatusReady;
            await Db.SaveChangesAsync();
        }

        public async Task<AllowedPythonModuleResponse> CreatePipAsync(
            string importName, string? spec = null, bool? system = null, params Guid[] envs)
            => (await Modules.CreateAsync(new CreateAllowedPythonModule
            {
                ImportName = importName,
                Source = AllowedPythonModule.SourcePip,
                PipSpec = spec,
                InstallInSystem = system,
                EnvironmentIds = envs.ToList(),
            })).Value!;

        public async Task<PythonEnvironmentResponse> GetEnvAsync(Guid id)
            => (await Environments.GetAsync(id)).Value!;

        public void Dispose() => Db.Dispose();
    }

    // ─── effective requirements: merge, dedup, hash ─────────────────────

    [Fact]
    public void Effective_requirements_without_included_specs_equal_the_normalised_own_text()
    {
        var own = "netmiko>=4,<5\r\nparamiko>=3.5,<5";
        var effective = PythonEnvironmentText.BuildEffectiveRequirements(own, Array.Empty<string>());

        Assert.Equal(PythonEnvironmentText.NormalizeRequirements(own), effective);
        // An environment nothing targets keeps the hash it had before targets existed.
        Assert.Equal(
            PythonEnvironmentText.ComputeHash("3.12", PythonEnvironmentText.NormalizeRequirements(own)),
            PythonEnvironmentText.ComputeHash("3.12", effective));
    }

    [Fact]
    public void Effective_requirements_append_sorted_deduplicated_specs_under_a_header()
    {
        var effective = PythonEnvironmentText.BuildEffectiveRequirements(
            "netmiko>=4,<5\nParamiko >= 3.5 , <5  # pinned for ssh-rsa\n",
            new[] { "requests", "  pyyaml==6.0 ", "paramiko>=3.5,<5", "REQUESTS", "", null, "netmiko>=4,<5" });

        Assert.Equal(
            "netmiko>=4,<5\nParamiko >= 3.5 , <5  # pinned for ssh-rsa\n"
            + PythonEnvironmentText.IncludedHeader + "\n"
            + "pyyaml==6.0\nrequests\n",
            effective);
    }

    [Fact]
    public void Effective_requirements_hash_is_order_independent_and_changes_with_the_specs()
    {
        var a = PythonEnvironmentText.BuildEffectiveRequirements(EnvRequirements, new[] { "requests", "pyyaml" });
        var b = PythonEnvironmentText.BuildEffectiveRequirements(EnvRequirements, new[] { "pyyaml", "requests" });
        var c = PythonEnvironmentText.BuildEffectiveRequirements(EnvRequirements, new[] { "pyyaml" });

        Assert.Equal(PythonEnvironmentText.ComputeHash(null, a), PythonEnvironmentText.ComputeHash(null, b));
        Assert.NotEqual(PythonEnvironmentText.ComputeHash(null, a), PythonEnvironmentText.ComputeHash(null, c));
    }

    [Fact]
    public async Task Repository_effective_requirements_include_only_active_pip_rows_that_target_the_env()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var other = await f.ReadyEnvAsync("other");

        await f.CreatePipAsync("requests", envs: legacy);
        await f.CreatePipAsync("yaml", spec: "pyyaml==6.0", envs: new[] { legacy, other });
        await f.CreatePipAsync("bs4", spec: "beautifulsoup4", envs: other);
        // Inactive row (deactivated outside the API): contributes nothing.
        f.Db.AllowedPythonModules.Single(m => m.ImportName == "requests").IsActive = false;
        await f.Db.SaveChangesAsync();

        var env = f.Db.PythonEnvironments.Single(e => e.PythonEnvironmentId == legacy);
        var effective = await f.EnvRepo.GetEffectiveRequirementsAsync(env);

        Assert.Equal(EnvRequirements + PythonEnvironmentText.IncludedHeader + "\npyyaml==6.0\n", effective);
    }

    [Fact]
    public async Task Environment_response_lists_the_included_packages()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var other = await f.ReadyEnvAsync("other");
        await f.CreatePipAsync("yaml", spec: "pyyaml==6.0", envs: legacy);
        await f.CreatePipAsync("requests", envs: legacy);

        Assert.Equal(new[] { "pyyaml==6.0", "requests" }, (await f.GetEnvAsync(legacy)).IncludedPackages);
        Assert.Empty((await f.GetEnvAsync(other)).IncludedPackages);

        var page = (ListResponse<PythonEnvironmentResponse>)Assert.IsType<OkObjectResult>(
            (await f.Environments.ListAsync(50, 0)).Result).Value!;
        Assert.Equal(2, page.Data.Single(e => e.Name == "ssh-legacy").IncludedPackages.Count);
    }

    // ─── target changes mark only the affected environments pending ─────

    [Fact]
    public async Task Creating_a_row_marks_only_its_target_environments_pending()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var other = await f.ReadyEnvAsync("other");

        var created = await f.CreatePipAsync("requests", envs: legacy);

        Assert.Equal(PythonEnvironment.StatusPending, f.Status(legacy));
        Assert.Equal(PythonEnvironment.StatusReady, f.Status(other));
        Assert.True(created.InstallInSystem);
        var target = Assert.Single(created.Environments);
        Assert.Equal("ssh-legacy", target.Name);
        Assert.Equal(PythonEnvironment.StatusPending, target.Status);
        Assert.Equal(new[] { legacy }, created.EnvironmentIds);
    }

    [Fact]
    public async Task Changing_targets_marks_added_and_removed_environments_pending_and_nothing_else()
    {
        using var f = new Fixture();
        var a = await f.ReadyEnvAsync("env-a");
        var b = await f.ReadyEnvAsync("env-b");
        var c = await f.ReadyEnvAsync("env-c");
        var row = await f.CreatePipAsync("requests", envs: new[] { a, c });
        await f.MarkAllReadyAsync();

        // a removed, b added, c unchanged.
        var updated = (await f.Modules.UpdateAsync(row.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { EnvironmentIds = new List<Guid> { b, c } })).Value!;

        Assert.Equal(PythonEnvironment.StatusPending, f.Status(a));
        Assert.Equal(PythonEnvironment.StatusPending, f.Status(b));
        Assert.Equal(PythonEnvironment.StatusReady, f.Status(c));
        Assert.Equal(new[] { "env-b", "env-c" }, updated.Environments.Select(t => t.Name));
        Assert.Equal(2, f.Db.PythonModuleEnvironments.Count());
    }

    [Fact]
    public async Task Removing_the_last_target_marks_the_old_environment_pending()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var row = await f.CreatePipAsync("requests", envs: legacy);
        await f.MarkAllReadyAsync();

        await f.Modules.UpdateAsync(row.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { EnvironmentIds = new List<Guid>() });

        Assert.Equal(PythonEnvironment.StatusPending, f.Status(legacy));
        Assert.Empty(f.Db.PythonModuleEnvironments);
        Assert.Empty((await f.GetEnvAsync(legacy)).IncludedPackages);
    }

    [Fact]
    public async Task Changing_the_pip_spec_rebuilds_every_target_and_reinstalls_in_system()
    {
        using var f = new Fixture();
        var a = await f.ReadyEnvAsync("env-a");
        var b = await f.ReadyEnvAsync("env-b");
        var untouched = await f.ReadyEnvAsync("env-c");
        var row = await f.CreatePipAsync("requests", envs: new[] { a, b });
        await f.MarkAllReadyAsync();
        var module = f.Db.AllowedPythonModules.Single();
        module.Status = AllowedPythonModule.StatusReady;
        await f.Db.SaveChangesAsync();

        var updated = (await f.Modules.UpdateAsync(row.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { PipSpec = "requests==2.32.3" })).Value!;

        Assert.Equal(PythonEnvironment.StatusPending, f.Status(a));
        Assert.Equal(PythonEnvironment.StatusPending, f.Status(b));
        Assert.Equal(PythonEnvironment.StatusReady, f.Status(untouched));
        Assert.Equal(AllowedPythonModule.StatusPending, updated.Status);
        Assert.Equal(new[] { "requests==2.32.3" }, (await f.GetEnvAsync(a)).IncludedPackages);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_rebuilds_nothing()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var row = await f.CreatePipAsync("requests", envs: legacy);
        await f.MarkAllReadyAsync();

        await f.Modules.UpdateAsync(row.AllowedPythonModuleId, new UpdateAllowedPythonModule
        {
            PipSpec = "requests",
            EnvironmentIds = new List<Guid> { legacy },
        });

        Assert.Equal(PythonEnvironment.StatusReady, f.Status(legacy));
    }

    [Fact]
    public async Task Deleting_a_row_marks_its_target_environments_pending()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var other = await f.ReadyEnvAsync("other");
        var row = await f.CreatePipAsync("requests", envs: legacy);
        await f.MarkAllReadyAsync();

        Assert.IsType<NoContentResult>(await f.Modules.DeleteAsync(row.AllowedPythonModuleId));

        Assert.Equal(PythonEnvironment.StatusPending, f.Status(legacy));
        Assert.Equal(PythonEnvironment.StatusReady, f.Status(other));
        Assert.Empty(f.Db.PythonModuleEnvironments);
    }

    // ─── validation of environment ids ──────────────────────────────────

    [Fact]
    public async Task Unknown_environment_ids_are_rejected_with_a_400()
    {
        using var f = new Fixture();
        var unknown = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => f.CreatePipAsync("requests", envs: unknown));

        Assert.Equal("environment_ids_invalid", ex.Code);
        Assert.Equal(400, ex.Status);
        Assert.Contains(unknown.ToString(), ex.Message);
        Assert.Empty(f.Db.AllowedPythonModules);
    }

    [Fact]
    public async Task Inactive_environment_ids_are_rejected_on_create_and_update()
    {
        using var f = new Fixture();
        var deleted = await f.ReadyEnvAsync("old-env");
        await f.Environments.DeleteAsync(deleted);
        var live = await f.ReadyEnvAsync("live-env");

        var onCreate = await Assert.ThrowsAsync<ValidationException>(() => f.CreatePipAsync("requests", envs: deleted));
        Assert.Equal("environment_ids_invalid", onCreate.Code);

        var row = await f.CreatePipAsync("requests", envs: live);
        var onUpdate = await Assert.ThrowsAsync<ValidationException>(() => f.Modules.UpdateAsync(
            row.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { EnvironmentIds = new List<Guid> { live, deleted } }));
        Assert.Equal("environment_ids_invalid", onUpdate.Code);
        Assert.Single(f.Db.PythonModuleEnvironments);
    }

    [Fact]
    public async Task A_stdlib_row_takes_no_environment_targets()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => f.Modules.CreateAsync(new CreateAllowedPythonModule
        {
            ImportName = "json",
            Source = AllowedPythonModule.SourceStdlib,
            EnvironmentIds = new List<Guid> { legacy },
        }));
        Assert.Equal("targets_not_applicable", ex.Code);

        var stdlib = (await f.Modules.CreateAsync(new CreateAllowedPythonModule
        {
            ImportName = "json",
            Source = AllowedPythonModule.SourceStdlib,
        })).Value!;
        Assert.False(stdlib.InstallInSystem);
        Assert.Empty(stdlib.Environments);
        await Assert.ThrowsAsync<ValidationException>(() => f.Modules.UpdateAsync(
            stdlib.AllowedPythonModuleId, new UpdateAllowedPythonModule { InstallInSystem = false }));
    }

    // ─── system target off ──────────────────────────────────────────────

    [Fact]
    public async Task Without_the_system_target_a_row_is_ready_and_the_provisioner_skips_it()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");

        var row = await f.CreatePipAsync("yaml", spec: "pyyaml==6.0", system: false, envs: legacy);

        Assert.False(row.InstallInSystem);
        // Status tracks the system install only — nothing to wait for.
        Assert.Equal(AllowedPythonModule.StatusReady, row.Status);
        var entity = f.Db.AllowedPythonModules.Single();
        Assert.False(PythonPackageProvisionerHostedService.ShouldInstallInSystem(entity));

        // Retry has no system install to re-queue.
        var retried = (await f.Modules.RetryAsync(row.AllowedPythonModuleId)).Value!;
        Assert.Equal(AllowedPythonModule.StatusReady, retried.Status);
    }

    [Fact]
    public void The_provisioner_installs_only_pip_rows_with_the_system_target()
    {
        var pip = new AllowedPythonModule { Source = AllowedPythonModule.SourcePip, PipSpec = "requests" };
        Assert.True(PythonPackageProvisionerHostedService.ShouldInstallInSystem(pip));

        pip.InstallInSystem = false;
        Assert.False(PythonPackageProvisionerHostedService.ShouldInstallInSystem(pip));

        Assert.False(PythonPackageProvisionerHostedService.ShouldInstallInSystem(
            new AllowedPythonModule { Source = AllowedPythonModule.SourceStdlib }));
    }

    [Fact]
    public async Task Toggling_the_system_target_requeues_or_releases_the_system_install()
    {
        using var f = new Fixture();
        var row = await f.CreatePipAsync("requests");
        Assert.Equal(AllowedPythonModule.StatusPending, row.Status);

        var off = (await f.Modules.UpdateAsync(row.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { InstallInSystem = false })).Value!;
        Assert.False(off.InstallInSystem);
        Assert.Equal(AllowedPythonModule.StatusReady, off.Status);

        var on = (await f.Modules.UpdateAsync(row.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { InstallInSystem = true })).Value!;
        Assert.True(on.InstallInSystem);
        Assert.Equal(AllowedPythonModule.StatusPending, on.Status);
    }

    [Fact]
    public async Task Without_the_system_target_the_import_name_must_be_a_real_identifier()
    {
        using var f = new Fixture();

        // Only the system install discovers the import name of a package-named row.
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            f.CreatePipAsync("python-dateutil", system: false));
        Assert.Equal("import_name_required", ex.Code);

        Assert.NotNull(await f.CreatePipAsync("dateutil", spec: "python-dateutil", system: false));
    }

    // ─── builder stale-claim guard ──────────────────────────────────────

    [Fact]
    public void Stale_claim_guard_compares_the_effective_requirements()
    {
        var env = new PythonEnvironment
        {
            PythonEnvironmentId = Guid.NewGuid(),
            Name = "ssh-legacy",
            Requirements = EnvRequirements,
            Status = PythonEnvironment.StatusBuilding,
            IsActive = true,
        };
        var claimedEffective = PythonEnvironmentText.BuildEffectiveRequirements(EnvRequirements, new[] { "requests" });
        var claim = PythonEnvironmentBuildGuard.Capture(env, claimedEffective);

        Assert.True(PythonEnvironmentBuildGuard.IsStillCurrent(claim, env, claimedEffective));

        // Own requirements identical, but a target was added during the build.
        var withTarget = PythonEnvironmentText.BuildEffectiveRequirements(EnvRequirements, new[] { "requests", "pyyaml" });
        Assert.False(PythonEnvironmentBuildGuard.IsStillCurrent(claim, env, withTarget));

        // ...or removed.
        Assert.False(PythonEnvironmentBuildGuard.IsStillCurrent(claim, env, EnvRequirements));

        // The usual reasons still apply.
        Assert.False(PythonEnvironmentBuildGuard.IsStillCurrent(claim, null, null));
        env.Status = PythonEnvironment.StatusPending;
        Assert.False(PythonEnvironmentBuildGuard.IsStillCurrent(claim, env, claimedEffective));
    }

    [Fact]
    public async Task A_target_change_during_a_build_invalidates_the_claim()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var env = f.Db.PythonEnvironments.Single();
        env.Status = PythonEnvironment.StatusBuilding; // as the builder's claim leaves it
        await f.Db.SaveChangesAsync();
        var claim = PythonEnvironmentBuildGuard.Capture(env, await f.EnvRepo.GetEffectiveRequirementsAsync(env));

        // The admin targets the environment while it builds.
        await f.CreatePipAsync("requests", envs: legacy);

        var current = f.Db.PythonEnvironments.Single();
        var currentEffective = await f.EnvRepo.GetEffectiveRequirementsAsync(current);
        Assert.Equal(PythonEnvironment.StatusPending, current.Status);
        Assert.NotEqual(claim.EffectiveRequirements, currentEffective);
        Assert.False(PythonEnvironmentBuildGuard.IsStillCurrent(claim, current, currentEffective));
    }

    // ─── environment delete ─────────────────────────────────────────────

    [Fact]
    public async Task Deleting_an_environment_drops_its_targets_instead_of_refusing()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        var other = await f.ReadyEnvAsync("other");
        var row = await f.CreatePipAsync("requests", envs: new[] { legacy, other });

        Assert.IsType<NoContentResult>(await f.Environments.DeleteAsync(legacy));

        var link = Assert.Single(f.Db.PythonModuleEnvironments);
        Assert.Equal(other, link.PythonEnvironmentId);
        var page = (ListResponse<AllowedPythonModuleResponse>)Assert.IsType<OkObjectResult>(
            (await f.Modules.ListAsync(50, 0)).Result).Value!;
        Assert.Equal(new[] { "other" }, page.Data.Single().Environments.Select(t => t.Name));
        Assert.Equal(row.AllowedPythonModuleId, page.Data.Single().AllowedPythonModuleId);
    }

    // ─── masking ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pip_specs_echoed_from_rows_are_credential_masked()
    {
        using var f = new Fixture();
        var legacy = await f.ReadyEnvAsync("ssh-legacy");
        // PipSpecRe cannot admit a URL; a row written before it (or by hand)
        // still must never echo its credentials.
        var module = new AllowedPythonModule
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            ImportName = "internal_pkg",
            Source = AllowedPythonModule.SourcePip,
            PipSpec = "internal-pkg @ https://deploy:s3cret@pkgs.example.com/internal_pkg-1.0-py3-none-any.whl",
            Status = AllowedPythonModule.StatusReady,
            Error = "pip install failed: 401 from https://deploy:s3cret@pkgs.example.com/",
        };
        module.EnvironmentLinks.Add(new PythonModuleEnvironment
        {
            AllowedPythonModuleId = module.AllowedPythonModuleId,
            PythonEnvironmentId = legacy,
        });
        f.Db.AllowedPythonModules.Add(module);
        await f.Db.SaveChangesAsync();

        var envResponse = await f.GetEnvAsync(legacy);
        var included = Assert.Single(envResponse.IncludedPackages);
        Assert.DoesNotContain("s3cret", included);
        Assert.Contains("https://***@pkgs.example.com", included);

        var page = (ListResponse<AllowedPythonModuleResponse>)Assert.IsType<OkObjectResult>(
            (await f.Modules.ListAsync(50, 0)).Result).Value!;
        var row = page.Data.Single();
        Assert.DoesNotContain("s3cret", row.PipSpec);
        Assert.DoesNotContain("s3cret", row.Error);

        // The audit row of a later edit masks it too.
        await f.Modules.UpdateAsync(module.AllowedPythonModuleId,
            new UpdateAllowedPythonModule { EnvironmentIds = new List<Guid>() });
        Assert.DoesNotContain(f.Audit.Entries, e => e.Payload.Contains("s3cret"));

        // The builder installs the real spec (it needs the credentials); its
        // error text is masked by FailAsync before it is stored.
        var effective = PythonEnvironmentText.BuildEffectiveRequirements(EnvRequirements, new[] { module.PipSpec });
        Assert.Contains("s3cret", effective);
        Assert.DoesNotContain("s3cret", PythonEnvironmentText.MaskCredentials("uv failed:\n" + effective));
    }
}
