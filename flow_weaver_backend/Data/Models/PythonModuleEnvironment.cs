namespace flow_weaver_backend.Models;

// Join row: allowlist module → uv environment it is installed in (an install
// target, see AllowedPythonModule.InstallInSystem / EnvironmentLinks).
//
// Deleting the module cascades its rows. An environment is soft-deleted, so
// PythonEnvironmentService.DeleteAsync drops its rows explicitly — a module
// target never blocks an environment delete.
public class PythonModuleEnvironment
{
    public Guid AllowedPythonModuleId { get; set; }
    public AllowedPythonModule? Module { get; set; }

    public Guid PythonEnvironmentId { get; set; }
    public PythonEnvironment? Environment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
