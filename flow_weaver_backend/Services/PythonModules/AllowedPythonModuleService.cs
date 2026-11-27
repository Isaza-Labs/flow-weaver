using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.PythonEnvironments;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.PythonModules;

// Admin CRUD for the python_snippet import allow-list. The controller
// is gated to the Admin policy, so this service assumes the caller is already an
// admin and only enforces business rules (validation, uniqueness, lifecycle).
//
// stdlib modules are `ready` immediately (nothing to install); pip modules start
// `pending` and the PythonPackageProvisioner (worker) installs PipSpec into the
// package dir, flipping them to `ready` or `failed`.
//
// Install targets (pip rows only): "System Python" (InstallInSystem, the
// default) and zero or more uv environments (python_module_environments). An
// environment never gets a package ad hoc: the row's spec joins that
// environment's effective requirements, so every change here that alters an
// environment's effective requirements — adding/removing a target, changing
// the spec, deleting the row — marks the affected environments `pending` in
// the same SaveChanges, and the builder rebuilds them.
public interface IAllowedPythonModuleService
{
    Task<ActionResult<ListResponse<AllowedPythonModuleResponse>>> ListAsync(int limit, int offset);
    Task<ActionResult<AllowedPythonModuleResponse>> CreateAsync(CreateAllowedPythonModule dto);
    Task<ActionResult<AllowedPythonModuleResponse>> UpdateAsync(Guid id, UpdateAllowedPythonModule dto);
    Task<IActionResult> DeleteAsync(Guid id);
    Task<ActionResult<AllowedPythonModuleResponse>> RetryAsync(Guid id);
}

public class AllowedPythonModuleService : IAllowedPythonModuleService
{
    // Top-level Python module identifier: starts with a letter/underscore, then
    // letters/digits/underscores. No dots — only the top-level package is
    // checked by the import guard.
    private static readonly Regex ImportNameRe = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // A pip distribution name (PEP 503). Accepted for a pip row because the import
    // name is usually NOT the package name — python-dateutil imports as dateutil,
    // beautifulsoup4 as bs4, Pillow as PIL — and nothing derives one from the other.
    // The provisioner asks the installed distribution what it provides and points the
    // row at that, so an admin types the name they have rather than the one they
    // would have to look up.
    private static readonly Regex DistributionNameRe =
        new(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    // pip requirement specifier charset: package name + extras + version pins.
    // Deliberately excludes shell metacharacters and whitespace. We pass it to
    // pip via ArgumentList (no shell), so this is defense in depth.
    private static readonly Regex PipSpecRe = new(@"^[A-Za-z0-9._\-\[\]=<>!~,]+$", RegexOptions.Compiled);

    private readonly IAllowedPythonModuleRepository _modules;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;

    public AllowedPythonModuleService(
        IAllowedPythonModuleRepository modules, ICurrentUser caller, IAuditLogger audit)
    {
        _modules = modules;
        _caller = caller;
        _audit = audit;
    }

    public async Task<ActionResult<ListResponse<AllowedPythonModuleResponse>>> ListAsync(int limit, int offset)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _modules.CountAsync();
        var rows = await _modules.ListWithTargetsAsync(limit, offset);
        return new OkObjectResult(new ListResponse<AllowedPythonModuleResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<AllowedPythonModuleResponse>> CreateAsync(CreateAllowedPythonModule dto)
    {
        var importName = (dto.ImportName ?? string.Empty).Trim();

        var source = (dto.Source ?? AllowedPythonModule.SourcePip).Trim().ToLowerInvariant();
        if (source != AllowedPythonModule.SourceStdlib && source != AllowedPythonModule.SourcePip)
            throw new ValidationException("source must be 'stdlib' or 'pip'.", code: "source_invalid");

        // A pip row may be named the way pip names it; the import name is discovered
        // on install. A stdlib row has no installer to ask, so its name has to be the
        // one an import statement resolves to.
        var acceptable = ImportNameRe.IsMatch(importName)
            || (source == AllowedPythonModule.SourcePip && DistributionNameRe.IsMatch(importName));
        if (!acceptable)
            throw new ValidationException(
                source == AllowedPythonModule.SourcePip
                    ? "import_name must be a package name (letters, digits and . _ -) or a "
                      + "top-level Python module identifier."
                    : "import_name must be a top-level Python module identifier (letters, digits, "
                      + "underscore; no dots). For a pip package choose source 'pip' — the package "
                      + "name is then accepted and the import name is read off the install.",
                code: "import_name_invalid");

        string? pipSpec = null;
        if (source == AllowedPythonModule.SourcePip)
            pipSpec = NormalizePipSpec(dto.PipSpec, importName);

        // Targets exist only where there is something to install.
        var installInSystem = true;
        var environmentIds = new List<Guid>();
        if (source == AllowedPythonModule.SourceStdlib)
        {
            if (dto.EnvironmentIds is { Count: > 0 })
                throw new ValidationException(
                    "environment_ids applies to pip modules only — a stdlib module has nothing to install.",
                    code: "targets_not_applicable");
        }
        else
        {
            installInSystem = dto.InstallInSystem ?? true;
            environmentIds = (dto.EnvironmentIds ?? new List<Guid>()).Distinct().ToList();
            EnsureImportNameUsableWithoutSystem(importName, installInSystem);
            await EnsureActiveEnvironmentsAsync(environmentIds);
        }

        var existing = await _modules.FindByImportNameAsync(importName);
        if (existing is not null)
            throw new ValidationException(
                $"module '{importName}' is already on the allow-list.", code: "import_name_duplicate");

        var now = DateTime.UtcNow;
        var module = new AllowedPythonModule
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            ImportName = importName,
            Source = source,
            PipSpec = pipSpec,
            InstallInSystem = installInSystem,
            // stdlib needs no install — usable at once. pip waits on the
            // provisioner, unless the system target is off: then there is no
            // system install to wait for (Status tracks only that one).
            Status = source == AllowedPythonModule.SourcePip && installInSystem
                ? AllowedPythonModule.StatusPending
                : AllowedPythonModule.StatusReady,
            CreatedBy = _caller.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var envId in environmentIds)
            module.EnvironmentLinks.Add(new PythonModuleEnvironment
            {
                AllowedPythonModuleId = module.AllowedPythonModuleId,
                PythonEnvironmentId = envId,
                CreatedAt = now,
            });
        _modules.Add(module);
        var rebuilt = await _modules.MarkEnvironmentsPendingAsync(environmentIds);
        await _modules.SaveChangesAsync();

        // Approving a pip package makes the worker install and then execute
        // third-party code inside the snippet sandbox. That is the highest-
        // privilege action an admin can take here and it left no trail at all.
        // PipSpec is recorded as written (credentials masked) — the exact
        // version pin is the artefact an incident review needs.
        await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "create",
            after: AuditView(module, rebuilt));

        return await ReloadResponseAsync(module.AllowedPythonModuleId);
    }

    public async Task<ActionResult<AllowedPythonModuleResponse>> UpdateAsync(Guid id, UpdateAllowedPythonModule dto)
    {
        var module = await _modules.GetWithTargetsAsync(id, tracking: true)
            ?? throw new NotFoundException("allowed python module", id);

        if (module.Source != AllowedPythonModule.SourcePip)
            throw new ValidationException(
                "only pip modules can be edited — a stdlib module has no pip_spec or install targets.",
                code: "targets_not_applicable");

        // Validate everything before mutating the tracked entity.
        var newSpec = dto.PipSpec is null ? module.PipSpec : NormalizePipSpec(dto.PipSpec, module.ImportName);
        var newSystem = dto.InstallInSystem ?? module.InstallInSystem;
        var oldEnvIds = module.EnvironmentLinks.Select(l => l.PythonEnvironmentId).ToHashSet();
        var newEnvIds = dto.EnvironmentIds is null
            ? oldEnvIds.ToHashSet()
            : dto.EnvironmentIds.Distinct().ToHashSet();

        EnsureImportNameUsableWithoutSystem(module.ImportName, newSystem);
        if (dto.EnvironmentIds is not null)
            await EnsureActiveEnvironmentsAsync(newEnvIds.ToList());

        var specChanged = !string.Equals(newSpec, module.PipSpec, StringComparison.Ordinal);
        var systemTurnedOn = newSystem && !module.InstallInSystem;
        var systemTurnedOff = !newSystem && module.InstallInSystem;

        // Environments whose effective requirements change: with a new spec,
        // every one the row targets before or after; otherwise only the ones
        // added or removed.
        var affected = specChanged
            ? oldEnvIds.Union(newEnvIds).ToList()
            : oldEnvIds.Except(newEnvIds).Union(newEnvIds.Except(oldEnvIds)).ToList();

        var before = AuditView(module, rebuilt: null);
        var now = DateTime.UtcNow;

        module.PipSpec = newSpec;
        module.InstallInSystem = newSystem;

        foreach (var link in module.EnvironmentLinks.Where(l => !newEnvIds.Contains(l.PythonEnvironmentId)).ToList())
            module.EnvironmentLinks.Remove(link);
        foreach (var envId in newEnvIds.Where(e => !oldEnvIds.Contains(e)))
            module.EnvironmentLinks.Add(new PythonModuleEnvironment
            {
                AllowedPythonModuleId = module.AllowedPythonModuleId,
                PythonEnvironmentId = envId,
                CreatedAt = now,
            });

        if (newSystem && (specChanged || systemTurnedOn))
        {
            // (Re)install into the system package dir.
            module.Status = AllowedPythonModule.StatusPending;
            module.Error = null;
        }
        else if (systemTurnedOff)
        {
            // No system install to wait for any more. Files an earlier install
            // left in <PackagesDir>/site stay there: the provisioner has no
            // uninstall (documented in the UI help).
            module.Status = AllowedPythonModule.StatusReady;
            module.Error = null;
        }

        module.UpdatedAt = now;
        var rebuilt = await _modules.MarkEnvironmentsPendingAsync(affected);
        await _modules.SaveChangesAsync();

        await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "update",
            before: before, after: AuditView(module, rebuilt));

        return await ReloadResponseAsync(module.AllowedPythonModuleId);
    }

    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var module = await _modules.GetWithTargetsAsync(id, tracking: true)
            ?? throw new NotFoundException("allowed python module", id);

        var before = AuditView(module, rebuilt: null);
        var envIds = module.EnvironmentLinks.Select(l => l.PythonEnvironmentId).ToList();

        // Hard delete: the unique (ImportName) index counts inactive
        // rows, so a soft-delete would block re-adding the same name. The
        // provisioner cleans up the installed package dir on the next sweep.
        // The environment links cascade; the environments the row targeted
        // lose its spec from their effective requirements, so they rebuild.
        _modules.Remove(module);
        var rebuilt = await _modules.MarkEnvironmentsPendingAsync(envIds);
        await _modules.SaveChangesAsync();

        // Hard delete, so this audit row is the ONLY surviving record that the
        // module was ever allowed.
        await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "delete",
            before: before, after: new { rebuilt_environments = rebuilt });

        return new NoContentResult();
    }

    public async Task<ActionResult<AllowedPythonModuleResponse>> RetryAsync(Guid id)
    {
        var module = await _modules.GetWithTargetsAsync(id, tracking: true)
            ?? throw new NotFoundException("allowed python module", id);

        // Only a pip module with the system target has a system install to
        // retry. (An environment target is retried with that environment's
        // Rebuild.) Re-queue it for the provisioner.
        if (module.Source == AllowedPythonModule.SourcePip && module.InstallInSystem)
        {
            module.Status = AllowedPythonModule.StatusPending;
            module.Error = null;
            module.UpdatedAt = DateTime.UtcNow;
            await _modules.SaveChangesAsync();

            await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "retry_install",
                after: new
                {
                    module.ImportName,
                    PipSpec = PythonEnvironmentText.MaskCredentials(module.PipSpec),
                    module.Status,
                });
        }
        return ToResponse(module);
    }

    // ─── rules ──────────────────────────────────────────────────────────

    private static string NormalizePipSpec(string? raw, string importName)
    {
        var pipSpec = string.IsNullOrWhiteSpace(raw) ? importName : raw.Trim();
        if (!PipSpecRe.IsMatch(pipSpec))
            throw new ValidationException(
                "pip_spec contains invalid characters (allowed: letters, digits, . _ - [ ] = < > ! ~ ,).",
                code: "pip_spec_invalid");
        return pipSpec;
    }

    // The import name of a pip row named after its DISTRIBUTION
    // (python-dateutil) is only resolved by the system install, which asks
    // the installed package what it provides. Without that install nothing
    // would ever resolve it, so the row has to carry the real import name.
    private static void EnsureImportNameUsableWithoutSystem(string importName, bool installInSystem)
    {
        if (!installInSystem && !ImportNameRe.IsMatch(importName))
            throw new ValidationException(
                $"'{importName}' is a package name, not an import name. Without the System Python "
                + "target nothing discovers the import name, so set import_name to what scripts write "
                + "after `import` (e.g. 'dateutil' for python-dateutil) and pip_spec to the package.",
                code: "import_name_required");
    }

    private async Task EnsureActiveEnvironmentsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return;
        var found = (await _modules.ListActiveEnvironmentsAsync(ids))
            .Select(e => e.PythonEnvironmentId)
            .ToHashSet();
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
            throw new ValidationException(
                $"environment_ids: {string.Join(", ", missing)} "
                + (missing.Count == 1 ? "is not an active python environment." : "are not active python environments."),
                code: "environment_ids_invalid",
                details: missing.Select(m => m.ToString()).ToList());
    }

    private async Task<AllowedPythonModuleResponse> ReloadResponseAsync(Guid id)
    {
        var fresh = await _modules.GetWithTargetsAsync(id, tracking: false)
            ?? throw new NotFoundException("allowed python module", id);
        return ToResponse(fresh);
    }

    // Audit projection. The spec is masked like environment requirements —
    // PipSpecRe cannot admit a URL today, so this is defence in depth for
    // rows that predate it.
    private static object AuditView(AllowedPythonModule m, IReadOnlyList<string>? rebuilt) => new
    {
        m.ImportName,
        m.Source,
        PipSpec = PythonEnvironmentText.MaskCredentials(m.PipSpec),
        m.Status,
        install_in_system = m.Source == AllowedPythonModule.SourcePip && m.InstallInSystem,
        environment_ids = m.EnvironmentLinks.Select(l => l.PythonEnvironmentId).OrderBy(g => g).ToList(),
        rebuilt_environments = rebuilt,
    };

    internal static AllowedPythonModuleResponse ToResponse(AllowedPythonModule m)
    {
        var isPip = m.Source == AllowedPythonModule.SourcePip;
        var targets = isPip
            ? m.EnvironmentLinks
                .Where(l => l.Environment is { IsActive: true })
                .Select(l => new AllowedPythonModuleEnvironmentTarget
                {
                    PythonEnvironmentId = l.PythonEnvironmentId,
                    Name = l.Environment!.Name,
                    Status = l.Environment.Status,
                })
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList()
            : new List<AllowedPythonModuleEnvironmentTarget>();

        return new AllowedPythonModuleResponse
        {
            AllowedPythonModuleId = m.AllowedPythonModuleId,
            ImportName = m.ImportName,
            Source = m.Source,
            PipSpec = PythonEnvironmentText.MaskCredentials(m.PipSpec),
            Status = m.Status,
            InstalledVersion = m.InstalledVersion,
            Error = PythonEnvironmentText.MaskCredentials(m.Error),
            InstallInSystem = isPip && m.InstallInSystem,
            EnvironmentIds = targets.Select(t => t.PythonEnvironmentId).ToList(),
            Environments = targets,
            CreatedAt = m.CreatedAt,
            UpdatedAt = m.UpdatedAt,
        };
    }
}
