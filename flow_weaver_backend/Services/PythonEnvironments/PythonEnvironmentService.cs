using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonEnvironments;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.PythonEnvironments;

// CRUD for admin-defined Python environments. The controller gates writes to
// the Admin policy (defining an environment makes the worker download and
// install third-party packages — the same trust decision as approving a pip
// package), so this service only enforces business rules:
//   - name is a slug, unique among active rows, never `system`;
//   - at most one active environment is the SSH fallback;
//   - requirements are validated, and their URL credentials never leave the
//     server unmasked (responses, audit rows);
//   - editing requirements / python_version, or rebuild, puts the row back to
//     `pending` for the builder;
//   - responses carry `included_packages`: the pip specs Packages-allowlist
//     rows contribute to the environment's effective requirements (masked);
//   - a delete drops those module -> environment links (it is NOT refused
//     because rows target the environment).
public interface IPythonEnvironmentService
{
    Task<ActionResult<ListResponse<PythonEnvironmentResponse>>> ListAsync(int limit, int offset);
    Task<ActionResult<PythonEnvironmentResponse>> GetAsync(Guid id);
    Task<ActionResult<PythonEnvironmentResponse>> CreateAsync(CreatePythonEnvironment dto);
    Task<ActionResult<PythonEnvironmentResponse>> UpdateAsync(Guid id, UpdatePythonEnvironment dto);
    Task<IActionResult> DeleteAsync(Guid id);
    Task<ActionResult<PythonEnvironmentResponse>> RebuildAsync(Guid id);
}

public class PythonEnvironmentService : IPythonEnvironmentService
{
    private readonly IPythonEnvironmentRepository _environments;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;

    public PythonEnvironmentService(
        IPythonEnvironmentRepository environments, ICurrentUser caller, IAuditLogger audit)
    {
        _environments = environments;
        _caller = caller;
        _audit = audit;
    }

    public async Task<ActionResult<ListResponse<PythonEnvironmentResponse>>> ListAsync(int limit, int offset)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _environments.CountAsync();
        var rows = await _environments.ListAsync(limit, offset);
        var included = await _environments.ListIncludedSpecsAsync(
            rows.Select(r => r.PythonEnvironmentId).ToList());
        return new OkObjectResult(new ListResponse<PythonEnvironmentResponse>
        {
            Data = rows.Select(r => ToResponse(r, included[r.PythonEnvironmentId])).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<PythonEnvironmentResponse>> GetAsync(Guid id)
    {
        var env = await _environments.GetByIdAsync(id, tracking: false)
            ?? throw new NotFoundException("python environment", id);
        return await ToResponseAsync(env);
    }

    public async Task<ActionResult<PythonEnvironmentResponse>> CreateAsync(CreatePythonEnvironment dto)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        ValidateName(name);
        await EnsureNameFreeAsync(name, exceptId: null);

        var version = NormalizeVersion(dto.PythonVersion);
        var description = NormalizeDescription(dto.Description);

        // A brand-new row has no stored text to restore masked credentials
        // from, so a `***` here is a literal the admin pasted — refuse it.
        if (PythonEnvironmentText.ContainsMaskedCredentials(dto.Requirements))
            throw new ValidationException(
                "requirements contains masked credentials ('***') — type the real credentials.",
                code: "requirements_masked");
        var requirements = ValidateAndNormalizeRequirements(dto.Requirements);

        var isFallback = dto.IsSshFallback ?? false;
        if (isFallback) await EnsureSingleFallbackAsync(exceptId: null);

        var now = DateTime.UtcNow;
        var env = new PythonEnvironment
        {
            PythonEnvironmentId = Guid.NewGuid(),
            Name = name,
            Description = description,
            PythonVersion = version,
            Requirements = requirements,
            IsSshFallback = isFallback,
            Status = PythonEnvironment.StatusPending,
            CreatedBy = _caller.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _environments.Add(env);
        await _environments.SaveChangesAsync();

        // Defining an environment makes the worker download and install
        // third-party code — record exactly what was asked for (masked).
        await _audit.LogAsync("python_environment", env.PythonEnvironmentId, "create",
            after: AuditView(env));

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "PythonEnvironment",
            routeValues: new { id = env.PythonEnvironmentId },
            value: await ToResponseAsync(env));
    }

    public async Task<ActionResult<PythonEnvironmentResponse>> UpdateAsync(Guid id, UpdatePythonEnvironment dto)
    {
        var env = await _environments.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("python environment", id);

        // Validate everything before mutating the tracked entity.
        string? newName = null;
        if (dto.Name is not null)
        {
            newName = dto.Name.Trim();
            ValidateName(newName);
            if (!string.Equals(newName, env.Name, StringComparison.Ordinal))
                await EnsureNameFreeAsync(newName, exceptId: env.PythonEnvironmentId);
        }

        string? newRequirements = null;
        if (dto.Requirements is not null)
        {
            if (!PythonEnvironmentText.TryRestoreMaskedCredentials(
                    dto.Requirements, env.Requirements, out var restored, out var restoreError))
                throw new ValidationException(restoreError!, code: "requirements_masked");
            newRequirements = ValidateAndNormalizeRequirements(restored);
        }

        var versionSent = dto.PythonVersion is not null;
        var newVersion = versionSent ? NormalizeVersion(dto.PythonVersion) : env.PythonVersion;

        if (dto.IsSshFallback == true && !env.IsSshFallback)
            await EnsureSingleFallbackAsync(exceptId: env.PythonEnvironmentId);

        var before = AuditView(env);

        var renamed = newName is not null && !string.Equals(newName, env.Name, StringComparison.Ordinal);
        var definitionChanged =
            (newRequirements is not null && !string.Equals(newRequirements, env.Requirements, StringComparison.Ordinal))
            || (versionSent && !string.Equals(newVersion, env.PythonVersion, StringComparison.Ordinal));

        if (newName is not null) env.Name = newName;
        if (dto.Description is not null) env.Description = NormalizeDescription(dto.Description);
        if (newRequirements is not null) env.Requirements = newRequirements;
        if (versionSent) env.PythonVersion = newVersion;
        if (dto.IsSshFallback is not null) env.IsSshFallback = dto.IsSshFallback.Value;

        // A new definition (or a new directory name) needs a new build. The
        // previous good build stays on disk until the new one is swapped in.
        if (definitionChanged || renamed)
            ResetToPending(env);

        env.UpdatedAt = DateTime.UtcNow;
        await _environments.SaveChangesAsync();

        await _audit.LogAsync("python_environment", env.PythonEnvironmentId, "update",
            before: before, after: AuditView(env));

        return await ToResponseAsync(env);
    }

    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var env = await _environments.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("python environment", id);

        // A device or snippet pointing at a deleted environment fails every
        // run (the resolver refuses deleted envs rather than silently running
        // the system interpreter). Make the admin move them first.
        var (devices, snippets) = await _environments.CountReferencesAsync(id);
        if (devices > 0 || snippets > 0)
            throw new ConflictException(
                $"python environment '{env.Name}' is still used by {devices} device(s) and {snippets} "
                + "snippet(s) — switch them to another environment (or System default) first.",
                code: "python_environment_in_use");

        // Install targets are NOT a reason to refuse: the Packages rows that
        // targeted this environment simply stop targeting it (their other
        // targets are untouched).
        var droppedTargets = await _environments.RemoveModuleLinksAsync(id);

        // Soft delete. The name becomes free for a new environment; the build
        // directory is left for the builder to overwrite on the next build of
        // that name.
        env.IsActive = false;
        env.IsSshFallback = false;
        env.UpdatedAt = DateTime.UtcNow;
        await _environments.SaveChangesAsync();

        await _audit.LogAsync("python_environment", env.PythonEnvironmentId, "delete",
            before: AuditView(env), after: new { dropped_package_targets = droppedTargets });

        return new NoContentResult();
    }

    public async Task<ActionResult<PythonEnvironmentResponse>> RebuildAsync(Guid id)
    {
        var env = await _environments.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("python environment", id);

        ResetToPending(env);
        env.UpdatedAt = DateTime.UtcNow;
        await _environments.SaveChangesAsync();

        await _audit.LogAsync("python_environment", env.PythonEnvironmentId, "rebuild",
            after: AuditView(env));

        return await ToResponseAsync(env);
    }

    // ─── rules ──────────────────────────────────────────────────────────

    private static void ResetToPending(PythonEnvironment env)
    {
        env.Status = PythonEnvironment.StatusPending;
        env.LastError = null;
    }

    private static void ValidateName(string name)
    {
        if (!PythonEnvironmentText.IsValidName(name))
            throw new ValidationException(
                "name must be 3–50 characters of lowercase letters, digits and hyphens, start and end "
                + $"with a letter or digit, and not be '{PythonEnvironmentText.SystemName}' "
                + "(e.g. 'ssh-legacy').",
                code: "name_invalid");
    }

    private async Task EnsureNameFreeAsync(string name, Guid? exceptId)
    {
        var existing = await _environments.FindActiveByNameAsync(name);
        if (existing is not null && existing.PythonEnvironmentId != exceptId)
            throw new ValidationException(
                $"a python environment named '{name}' already exists.", code: "name_duplicate");
    }

    private async Task EnsureSingleFallbackAsync(Guid? exceptId)
    {
        var current = await _environments.FindActiveSshFallbackAsync(exceptId);
        if (current is not null)
            throw new ValidationException(
                $"'{current.Name}' is already the SSH fallback environment — only one environment can be. "
                + "Unset it there first.",
                code: "ssh_fallback_taken");
    }

    private static string? NormalizeVersion(string? raw)
    {
        var v = (raw ?? string.Empty).Trim();
        if (v.Length == 0) return null;
        if (!PythonEnvironmentText.IsValidPythonVersion(v))
            throw new ValidationException(
                "python_version must be a version such as '3.11' or '3.12.4' (or an interpreter name), "
                + "without spaces. Leave it empty to use the image's python3.",
                code: "python_version_invalid");
        return v;
    }

    private static string? NormalizeDescription(string? raw)
    {
        var d = (raw ?? string.Empty).Trim();
        if (d.Length == 0) return null;
        if (d.Length > PythonEnvironmentText.MaxDescriptionLength)
            throw new ValidationException(
                $"description is too long (max {PythonEnvironmentText.MaxDescriptionLength} characters).",
                code: "description_too_long");
        return d;
    }

    private static string ValidateAndNormalizeRequirements(string? raw)
    {
        var error = PythonEnvironmentText.ValidateRequirements(raw);
        if (error is not null)
            throw new ValidationException(error, code: "requirements_invalid");
        return PythonEnvironmentText.NormalizeRequirements(raw!);
    }

    // Audit projection — requirements MASKED, so an index token never lands
    // in audit_logs.
    private static object AuditView(PythonEnvironment e) => new
    {
        e.Name,
        e.Description,
        python_version = e.PythonVersion,
        requirements = PythonEnvironmentText.MaskCredentials(e.Requirements),
        is_ssh_fallback = e.IsSshFallback,
        e.Status,
    };

    private async Task<PythonEnvironmentResponse> ToResponseAsync(PythonEnvironment e)
    {
        var included = await _environments.ListIncludedSpecsAsync(new[] { e.PythonEnvironmentId });
        return ToResponse(e, included[e.PythonEnvironmentId]);
    }

    internal static PythonEnvironmentResponse ToResponse(
        PythonEnvironment e, IEnumerable<string>? includedPackages = null) => new()
    {
        PythonEnvironmentId = e.PythonEnvironmentId,
        Name = e.Name,
        Description = e.Description,
        PythonVersion = e.PythonVersion,
        Requirements = PythonEnvironmentText.MaskCredentials(e.Requirements) ?? string.Empty,
        IsSshFallback = e.IsSshFallback,
        Status = e.Status,
        LastError = PythonEnvironmentText.MaskCredentials(e.LastError),
        ResolvedPackages = PythonEnvironmentText.MaskCredentials(e.ResolvedPackages),
        RequirementsHash = e.RequirementsHash,
        // Same masking as requirements: a spec is echoed, never its credentials.
        IncludedPackages = (includedPackages ?? Enumerable.Empty<string>())
            .Select(spec => PythonEnvironmentText.MaskCredentials(spec) ?? string.Empty)
            .ToList(),
        BuiltAt = e.BuiltAt,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}
