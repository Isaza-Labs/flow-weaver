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

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
