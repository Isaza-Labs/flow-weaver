namespace flow_weaver_backend.Dtos;

// Execution constraints for Python subprocesses.
// Note: resource limits (RLIMIT_AS, RLIMIT_CPU, etc.) are POSIX-only.
// On Windows hosts the preamble still runs but the setrlimit calls are
// caught and ignored, so production workers should run on Linux.
public class SandboxConfig
{
    public int MaxMemoryMB { get; set; } = 512;
    public int MaxCPUSeconds { get; set; } = 300;
    public int MaxFileSizeMB { get; set; } = 10;
    public int MaxProcesses { get; set; } = 10;
    public bool NetworkEnabled { get; set; } = true;

    public static SandboxConfig Default => new();

    // Returns a Python snippet that sets resource limits using the
    // resource module. Inject at the top of every script before user code.
    public string ResourceLimitsPreamble() => $@"import resource as _fw_resource
# --- FlowWeaver sandbox resource limits ---
# Memory limit
_fw_mem_limit = {MaxMemoryMB} * 1024 * 1024
try:
    _fw_resource.setrlimit(_fw_resource.RLIMIT_AS, (_fw_mem_limit, _fw_mem_limit))
except (ValueError, OSError):
    pass  # RLIMIT_AS may not be available on all platforms
# CPU time limit
_fw_cpu_limit = {MaxCPUSeconds}
try:
    _fw_resource.setrlimit(_fw_resource.RLIMIT_CPU, (_fw_cpu_limit, _fw_cpu_limit))
except (ValueError, OSError):
    pass
# File size limit
_fw_file_limit = {MaxFileSizeMB} * 1024 * 1024
try:
    _fw_resource.setrlimit(_fw_resource.RLIMIT_FSIZE, (_fw_file_limit, _fw_file_limit))
except (ValueError, OSError):
    pass
# Max child processes
_fw_nproc_limit = {MaxProcesses}
try:
    _fw_resource.setrlimit(_fw_resource.RLIMIT_NPROC, (_fw_nproc_limit, _fw_nproc_limit))
except (ValueError, OSError):
    pass
del _fw_resource, _fw_mem_limit, _fw_cpu_limit, _fw_file_limit, _fw_nproc_limit
# --- End sandbox resource limits ---
";
}
