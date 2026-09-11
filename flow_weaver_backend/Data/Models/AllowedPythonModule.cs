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
}
