namespace flow_weaver_backend.Models;

// A Python module an admin has allowed `python_snippet` scripts to import,
// beyond the built-in stdlib safe-list baked into PythonHandler.
// Admins manage the list.
//
// Two names, deliberately separate:
//   ImportName — what the script writes (`import django`). This is what the
//                PythonHandler import check matches against.
//   PipSpec    — what pip installs (`Django==5.0`). Often differs from the
//                import name (import `bs4`, install `beautifulsoup4`).
//
// Source decides whether the provisioner has to install anything:
//   stdlib → already on the interpreter; allow the import, no install.
//   pip    → install PipSpec into the shared package dir before first use.
//
// Status is the provisioning lifecycle; only `ready` rows are honored by the
// PythonHandler import check, so a not-yet-installed package can't be imported.
public class AllowedPythonModule : BaseModel
{
    public const string SourceStdlib = "stdlib";
    public const string SourcePip = "pip";

    public const string StatusPending = "pending";
    public const string StatusInstalling = "installing";
    public const string StatusReady = "ready";
    public const string StatusFailed = "failed";

    public Guid AllowedPythonModuleId { get; set; }

    // Top-level module name the script imports (validated to a Python identifier).
    public string ImportName { get; set; } = string.Empty;

    // stdlib | pip.
    public string Source { get; set; } = SourcePip;

    // pip requirement specifier to install (e.g. "Django==5.0"). Null for
    // stdlib. Defaults to ImportName when omitted on a pip module.
    public string? PipSpec { get; set; }

    // pending | installing | ready | failed.
    public string Status { get; set; } = StatusPending;

    // Version pip actually resolved, filled in after a successful install.
    public string? InstalledVersion { get; set; }

    // Last provisioning error, when Status == failed.
    public string? Error { get; set; }

    // Admin who added it (audit trail).
    public Guid? CreatedBy { get; set; }

    // ─── Install targets (pip rows only) ────────────────────────────────
    //
    // Where PipSpec is installed. A stdlib row has no spec and no targets.
    //
    // InstallInSystem: the provisioner installs PipSpec into the system
    // interpreter's shared site dir (`pip --target <PackagesDir>/site`) — the
    // behaviour before targets existed, so the column defaults to true. When
    // false the provisioner skips the system install and the row is `ready`
    // as soon as it is saved (Status tracks the SYSTEM install only). Turning
    // it off later does NOT remove files a previous install left in the site
    // dir: the provisioner has no uninstall, so they stay importable from the
    // system interpreter until the volume is cleaned.
    public bool InstallInSystem { get; set; } = true;

    // uv environments that get PipSpec. The builder never pip-installs into a
    // venv ad hoc: an environment's EFFECTIVE requirements are its own
    // Requirements plus the specs of the rows that target it
    // (PythonEnvironmentText.BuildEffectiveRequirements), so changing a
    // target marks the environment `pending` and it rebuilds.
    public List<PythonModuleEnvironment> EnvironmentLinks { get; set; } = new();
}
