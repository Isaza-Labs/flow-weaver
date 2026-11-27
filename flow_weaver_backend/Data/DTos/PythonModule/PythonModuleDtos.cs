using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos.PythonModules;

// Admin request to allow a Python module in python_snippet scripts. For a pip
// module the worker installs `pip_spec` before the module becomes usable; for
// a stdlib module nothing is installed and it's ready immediately.
public class CreateAllowedPythonModule
{
    // Top-level module name the script imports (e.g. "django", "yaml", "bs4").
    [JsonPropertyName("import_name")]
    public string ImportName { get; set; } = string.Empty;

    // "stdlib" | "pip". Defaults to "pip".
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    // pip requirement to install (e.g. "Django==5.0", "beautifulsoup4").
    // Ignored for stdlib; defaults to import_name for pip.
    [JsonPropertyName("pip_spec")]
    public string? PipSpec { get; set; }

    // Install targets (pip rows only; refused on a stdlib row).
    // install_in_system: install into the system interpreter's package dir
    // (today's behaviour). Defaults to true.
    [JsonPropertyName("install_in_system")]
    public bool? InstallInSystem { get; set; }

    // uv environments (python_environment_id) that get pip_spec: it joins
    // each one's effective requirements and the environment rebuilds. Every
    // id must name an active environment.
    [JsonPropertyName("environment_ids")]
    public List<Guid>? EnvironmentIds { get; set; }
}

// PATCH-style edit of a pip row: every field is optional; omitted keeps the
// current value. Changing pip_spec re-queues the system install (when that
// target is on) and rebuilds every environment the row targets; changing the
// targets rebuilds the environments added AND removed.
public class UpdateAllowedPythonModule
{
    [JsonPropertyName("pip_spec")]
    public string? PipSpec { get; set; }

    // Turning it off does not uninstall what an earlier install put in the
    // system package dir (there is no uninstall); it only stops installing.
    [JsonPropertyName("install_in_system")]
    public bool? InstallInSystem { get; set; }

    // The full new set ([] = no environments).
    [JsonPropertyName("environment_ids")]
    public List<Guid>? EnvironmentIds { get; set; }
}

// One uv environment a row is installed in.
public class AllowedPythonModuleEnvironmentTarget
{
    [JsonPropertyName("python_environment_id")]
    public Guid PythonEnvironmentId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // The environment's build status (pending | building | ready | failed).
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public class AllowedPythonModuleResponse
{
    [JsonPropertyName("allowed_python_module_id")]
    public Guid AllowedPythonModuleId { get; set; }

    [JsonPropertyName("import_name")]
    public string ImportName { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("pip_spec")]
    public string? PipSpec { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("installed_version")]
    public string? InstalledVersion { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // System target. `status` / `installed_version` / `error` above describe
    // the system install; a row with this off is `ready` without one.
    // Always false for a stdlib row (no install anywhere).
    [JsonPropertyName("install_in_system")]
    public bool InstallInSystem { get; set; }

    [JsonPropertyName("environment_ids")]
    public List<Guid> EnvironmentIds { get; set; } = new();

    // The environment targets with their name and build status.
    [JsonPropertyName("environments")]
    public List<AllowedPythonModuleEnvironmentTarget> Environments { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
