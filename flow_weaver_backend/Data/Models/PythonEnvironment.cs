namespace flow_weaver_backend.Models;

// An admin-defined Python virtual environment, built at RUNTIME with uv into
// `<Python:PackagesDir>/envs/<Name>` by PythonEnvironmentBuilderHostedService.
//
// Why this exists: the image's system python3 carries ONE set of packages for
// every execution. paramiko 5.0 removed the `ssh-rsa` host-key algorithm, so a
// device whose sshd only offers ssh-rsa (e.g. Extreme EXOS on OpenSSH 8.1)
// cannot be reached with the image's paramiko — and downgrading it globally
// would hold every other device back. An environment pins its own packages
// (`ssh-legacy`: paramiko>=3.5,<5) and a Device / Snippet picks which one runs.
//
// Null environment EVERYWHERE means the image's system python3 — the behaviour
// before environments existed — so nothing changes for existing rows.
//
// Lifecycle (Status): pending → building → ready | failed. Editing
// Requirements or PythonVersion, or asking for a rebuild, puts the row back to
// `pending`; the builder claims it with a conditional UPDATE (same pattern as
// AllowedPythonModule: Status + UpdatedAt are the claim, a `building` row whose
// UpdatedAt went stale is reclaimable). A failed build keeps the previous good
// build on disk, so devices pointing at the env keep working.
public class PythonEnvironment : BaseModel
{
    public const string StatusPending = "pending";
    public const string StatusBuilding = "building";
    public const string StatusReady = "ready";
    public const string StatusFailed = "failed";

    public Guid PythonEnvironmentId { get; set; }

    // Slug used as the directory name: ^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$.
    // Unique among ACTIVE rows (enforced by the service; a soft-deleted row
    // does not block re-using its name).
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Passed to `uv venv --python`. Null = the image's python3.
    public string? PythonVersion { get; set; }

    // requirements.txt content. May contain index URLs with credentials, so it
    // is masked in every API response and audit row (PythonEnvironmentText).
    public string Requirements { get; set; } = string.Empty;

    // The environment the SSH runner retries with when the image's paramiko
    // cannot negotiate a host key. At most one ACTIVE row may set it
    // (enforced by PythonEnvironmentService).
    public bool IsSshFallback { get; set; }

    // pending | building | ready | failed.
    public string Status { get; set; } = StatusPending;

    // Last build error (truncated, credentials masked), when Status == failed.
    public string? LastError { get; set; }

    // `uv pip freeze` output of the last successful build.
    public string? ResolvedPackages { get; set; }

    // SHA-256 of (PythonVersion + Requirements) of the last successful build.
    public string? RequirementsHash { get; set; }

    // When the last successful build was swapped in.
    public DateTime? BuiltAt { get; set; }

    // Admin who created it (audit trail).
    public Guid? CreatedBy { get; set; }
}
