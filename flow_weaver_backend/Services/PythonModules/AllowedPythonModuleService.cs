using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.PythonModules;

// Admin CRUD for the python_snippet import allow-list. The controller
// is gated to the Admin policy, so this service assumes the caller is already an
// admin and only enforces business rules (validation, uniqueness, lifecycle).
//
// stdlib modules are `ready` immediately (nothing to install); pip modules start
// `pending` and the PythonPackageProvisioner (worker) installs PipSpec into the
// package dir, flipping them to `ready` or `failed`.
public interface IAllowedPythonModuleService
{
    Task<ActionResult<ListResponse<AllowedPythonModuleResponse>>> ListAsync(int limit, int offset);
    Task<ActionResult<AllowedPythonModuleResponse>> CreateAsync(CreateAllowedPythonModule dto);
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
        var rows = await _modules.ListAsync(limit, offset);
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
        {
            pipSpec = string.IsNullOrWhiteSpace(dto.PipSpec) ? importName : dto.PipSpec.Trim();
            if (!PipSpecRe.IsMatch(pipSpec))
                throw new ValidationException(
                    "pip_spec contains invalid characters (allowed: letters, digits, . _ - [ ] = < > ! ~ ,).",
                    code: "pip_spec_invalid");
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
            // stdlib needs no install — usable at once. pip waits on the provisioner.
            Status = source == AllowedPythonModule.SourceStdlib
                ? AllowedPythonModule.StatusReady
                : AllowedPythonModule.StatusPending,
            CreatedBy = _caller.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _modules.Add(module);
        await _modules.SaveChangesAsync();

        // Approving a pip package makes the worker install and then execute
        // third-party code inside the snippet sandbox. That is the highest-
        // privilege action an admin can take here and it left no trail at all.
        // PipSpec is recorded verbatim — the exact version pin is the artefact
        // an incident review needs.
        await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "create",
            after: new { module.ImportName, module.Source, module.PipSpec, module.Status });

        return ToResponse(module);
    }

    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var module = await _modules.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("allowed python module", id);

        // Hard delete: the unique (ImportName) index counts inactive
        // rows, so a soft-delete would block re-adding the same name. The
        // provisioner cleans up the installed package dir on the next sweep.
        _modules.Remove(module);
        await _modules.SaveChangesAsync();

        // Hard delete, so this audit row is the ONLY surviving record that the
        // module was ever allowed.
        await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "delete",
            before: new { module.ImportName, module.Source, module.PipSpec, module.Status });

        return new NoContentResult();
    }

    public async Task<ActionResult<AllowedPythonModuleResponse>> RetryAsync(Guid id)
    {
        var module = await _modules.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("allowed python module", id);

        // Only pip modules have an install to retry. Re-queue it for the provisioner.
        if (module.Source == AllowedPythonModule.SourcePip)
        {
            module.Status = AllowedPythonModule.StatusPending;
            module.Error = null;
            module.UpdatedAt = DateTime.UtcNow;
            await _modules.SaveChangesAsync();

            await _audit.LogAsync("allowed_python_module", module.AllowedPythonModuleId, "retry_install",
                after: new { module.ImportName, module.PipSpec, module.Status });
        }
        return ToResponse(module);
    }

    private static AllowedPythonModuleResponse ToResponse(AllowedPythonModule m) => new()
    {
        AllowedPythonModuleId = m.AllowedPythonModuleId,
        ImportName = m.ImportName,
        Source = m.Source,
        PipSpec = m.PipSpec,
        Status = m.Status,
        InstalledVersion = m.InstalledVersion,
        Error = m.Error,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };
}
