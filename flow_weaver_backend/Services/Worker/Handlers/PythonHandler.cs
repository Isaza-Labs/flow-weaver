using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Integration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Runs a Python script with a JSON payload on stdin. The script body lives
// on the Snippet row (edited in /snippets/{id}) and is delivered
// to the handler via `SnippetRequest.SnippetCode`. Per-step `InputPayload`
// is fed to the script on stdin and may override `timeout_seconds`; for
// backwards-compat we also accept `input.script` / `input.code` if a caller
// wants to pass an ad-hoc snippet.
//
// Security: the script is NOT eval'd inline. It is written to a temp file
// with a random name, executed as a child process, and the file is deleted
// after execution. No shell interpolation.
//
// Integration access: scripts request external systems by declaring
// `<name>_integration_id` keys in config_overrides. The handler resolves
// each one, renders the Authorization headers via IntegrationAuthBuilder,
// and injects a `_integrations` dict into stdin. The flowweaver_runtime
// library's `integration(name)` helper reads that dict to issue HTTP
// calls — the script never sees the raw token, and URLs are clamped to
// the integration's base_url so the sandbox can't reach anything the
// admin didn't pre-authorize.
public sealed class PythonHandler : ISnippetHandler
{
    public string Type => "python_snippet";
    // The script can call any registered integration; we can't tell
    // statically whether it's idempotent. Authors must downgrade
    // per-snippet when they know it's safe to retry.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private readonly IIntegrationRepository _integrations;
    private readonly IIntegrationAuthApplier _authApplier;
    private readonly ISecretResolver _secrets;
    private readonly IAllowedPythonModuleRepository _modules;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PythonHandler> _logger;

    public PythonHandler(
        IIntegrationRepository integrations,
        IIntegrationAuthApplier authApplier,
        ISecretResolver secrets,
        IAllowedPythonModuleRepository modules,
        IMemoryCache cache,
        IConfiguration configuration,
        IHostEnvironment env,
        ILogger<PythonHandler> logger)
    {
        _integrations = integrations;
        _authApplier = authApplier;
        _secrets = secrets;
        _modules = modules;
        _cache = cache;
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    // Loads the admin-approved extra import names (only `ready` rows),
    // cached briefly so a per-device fan-out doesn't hit the DB once per step. A
    // 30s TTL means an admin's allow-list change takes effect within 30s across
    // every worker process — no cross-process cache invalidation needed.
    private async Task<IReadOnlySet<string>> LoadAllowedModulesAsync(CancellationToken ct)
    {
        const string key = "py-allowed-modules";
        var cached = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var names = await _modules.ListReadyImportNamesAsync(ct);
            return (IReadOnlySet<string>)names.ToHashSet(StringComparer.Ordinal);
        });
        return cached ?? new HashSet<string>(StringComparer.Ordinal);
    }

    // The installed-package dir (Python:PackagesDir/site), matching where
    // PythonPackageProvisionerHostedService installs. Returns null when nothing
    // is installed there yet, so the sandbox skips the bind.
    private string? ResolvePackagesDir()
    {
        var root = (_configuration["Python:PackagesDir"] ?? "/app/pyenv").Trim();
        if (string.IsNullOrEmpty(root)) return null;
        var dir = Path.Combine(root, flow_weaver_backend.Services.Worker.PythonPackagesLayout.SiteDirName);
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()
                ? dir
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Sandbox modes:
    //   none      — bare python3, only safe if static analysis + ulimits are
    //               trusted to defend you. Allowed in Development. In any
    //               other environment we refuse to start the process.
    //   bwrap     — bubblewrap-based default. Read-only /usr /lib /lib64,
    //               private /tmp, no network namespace, no IPC, dies with
    //               the parent worker. The bwrap binary must be in the
    //               worker image (deploy/Dockerfile installs it under the
    //               python_snippet stage).
    //   nsjail    — admin supplies a config file via
    //               Python:NsjailConfig — bigger lift, but lets you pin
    //               capabilities, syscall filters, mount layout exactly.
    //   custom    — full passthrough via Python:SandboxCommand (legacy).
    //
    // CPU and memory caps are applied via a `prlimit --as=… --cpu=…`
    // wrapper layered ON TOP of the sandbox; the kernel enforces them
    // even if the script swaps `python3` for something else (it can't,
    // because the bwrap mounts are RO, but defense in depth).
    private (string Mode, string File, List<string> Args) ResolvePythonInvocation(string scriptPath, int timeoutSec, bool networkEnabled, string? pkgDir)
    {
        var mode = (_configuration["Python:SandboxMode"] ?? string.Empty).Trim().ToLowerInvariant();
        var custom = _configuration["Python:SandboxCommand"];

        // Backwards-compat: an explicit Python:SandboxCommand still wins.
        if (!string.IsNullOrWhiteSpace(custom)) mode = "custom";

        // Default selection: bwrap on Linux outside Development; bare on
        // Development for fast local iteration.
        if (string.IsNullOrEmpty(mode))
            mode = _env.IsDevelopment() ? "none" : "bwrap";

        var memMb = _configuration.GetValue("Python:MaxMemoryMb", 256);
        var cpuSec = Math.Max(1, timeoutSec); // wall-clock fallback if sandbox dies
        // RLIMIT_NPROC is per-UID, not per-process — it counts the worker's
        // OWN threads (the .NET runtime alone holds dozens) plus every
        // concurrent sandbox. A cap of 64 was below the worker's baseline
        // thread count, so bwrap's clone() for namespace setup intermittently
        // failed with EAGAIN ("Creating new namespace failed: Resource
        // temporarily unavailable"). Default high enough to clear that
        // baseline while still bounding a runaway fork bomb; tune per host.
        var maxProc = _configuration.GetValue("Python:MaxProcesses", DefaultMaxProcesses);

        var (innerFile, innerArgs) = mode switch
        {
            "bwrap" => BuildBwrap(scriptPath, networkEnabled, pkgDir),
            "nsjail" => BuildNsjail(scriptPath),
            "custom" => BuildCustom(custom!, scriptPath),
            _ => ("python3", new List<string> { scriptPath }),
        };

        // prlimit wraps everything: caps virtual memory, cpu time, and
        // file size so even a successful sandbox escape can't fork-bomb
        // or exhaust RAM. Skip when prlimit isn't available.
        if (HasPrlimit())
        {
            var args = new List<string>
            {
                $"--as={memMb * 1024L * 1024L}",
                $"--cpu={cpuSec}",
                "--fsize=10485760", // 10 MiB stdout cap (still piped, this is the on-disk cap)
                $"--nproc={maxProc}",
                "--",
                innerFile,
            };
            args.AddRange(innerArgs);
            return (mode, "prlimit", args);
        }

        return (mode, innerFile, innerArgs);
    }

    private static (string, List<string>) BuildBwrap(string scriptPath, bool networkEnabled, string? pkgDir)
    {
        // Default (networkEnabled=false): no /etc binds → no DNS, and
        // --unshare-all gives no network namespace even if the script imports
        // socket (the static analysis blocks it anyway). /tmp is a private
        // tmpfs so the script can't see prior runs.
        //
        // ORDER MATTERS: bwrap applies mounts left-to-right. The `--tmpfs
        // /tmp` MUST come BEFORE the script bind — the script lives at
        // /tmp/fw-*.py, so a tmpfs mounted on /tmp afterwards would shadow
        // the bind and python3 fails with "can't open file … No such file
        // or directory". Lay the tmpfs down first, then bind the script on
        // top of it.
        var args = new List<string>
        {
            "--ro-bind", "/usr", "/usr",
            "--ro-bind", "/lib", "/lib",
            "--ro-bind", "/lib64", "/lib64",
        };

        // NetworkEnabled (admin-gated, e.g. interactive netmiko SSH): bind the
        // resolver config read-only so DNS works. `--ro-bind-try` doesn't fail
        // if a file is absent (e.g. no nsswitch on a minimal image).
        if (networkEnabled)
        {
            args.AddRange(new[]
            {
                "--ro-bind-try", "/etc/resolv.conf", "/etc/resolv.conf",
                "--ro-bind-try", "/etc/hosts", "/etc/hosts",
                "--ro-bind-try", "/etc/nsswitch.conf", "/etc/nsswitch.conf",
            });
        }

        // Admin-approved pip packages: bind the install dir
        // read-only so the sandbox can import them (PYTHONPATH set below). The
        // dir lives outside the script's /tmp, so mount order vs the tmpfs is
        // irrelevant.
        if (!string.IsNullOrEmpty(pkgDir))
        {
            args.Add("--ro-bind");
            args.Add(pkgDir);
            args.Add(pkgDir);
        }

        args.AddRange(new[]
        {
            "--tmpfs", "/tmp",
            "--ro-bind", scriptPath, scriptPath,
            "--proc", "/proc",
            "--dev", "/dev",
            "--unshare-all",
        });

        // --share-net re-shares the host network namespace (only valid combined
        // with --unshare-all). Everything else stays unshared. Without it the
        // sandbox has no network at all.
        if (networkEnabled) args.Add("--share-net");

        // Make the installed packages importable (prepend so they win
        // over a same-named system module; keep the flowweaver_runtime bridge).
        if (!string.IsNullOrEmpty(pkgDir))
        {
            args.Add("--setenv");
            args.Add("PYTHONPATH");
            args.Add($"{pkgDir}:/usr/local/lib");
        }

        args.AddRange(new[]
        {
            "--die-with-parent",
            "--new-session",
            "--cap-drop", "ALL",
            "--",
            "python3",
            scriptPath,
        });
        return ("bwrap", args);
    }

    private (string, List<string>) BuildNsjail(string scriptPath)
    {
        var cfg = _configuration["Python:NsjailConfig"]
            ?? throw new InvalidOperationException("Python:SandboxMode=nsjail requires Python:NsjailConfig.");
        return ("nsjail", new List<string>
        {
            "--config", cfg,
            "--",
            "python3", scriptPath,
        });
    }

    private static (string, List<string>) BuildCustom(string custom, string scriptPath)
    {
        var parts = custom.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ("python3", new List<string> { scriptPath });
        var args = new List<string>(parts.Length);
        for (var i = 1; i < parts.Length; i++) args.Add(parts[i]);
        args.Add("python3");
        args.Add(scriptPath);
        return (parts[0], args);
    }

    private static bool HasPrlimit()
    {
        // Cheap one-shot probe so we don't pay the cost on every run.
        if (_prlimitProbed.HasValue) return _prlimitProbed.Value;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "prlimit",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(2000);
            _prlimitProbed = proc is { ExitCode: 0 };
        }
        catch { _prlimitProbed = false; }
        return _prlimitProbed.Value;
    }
    private static bool? _prlimitProbed;

    // ─────────────────────────────────────────────────────────────────
    //  Sandbox launch throttle + transient-setup retry
    // ─────────────────────────────────────────────────────────────────
    //
    // Every bwrap/nsjail launch creates fresh namespaces (bwrap's
    // --unshare-all does --unshare-user-try plus pid/ipc/net/uts/cgroup).
    // The host caps how many user namespaces can exist at once via
    // `user.max_user_namespaces` — a limit that is PER user-namespace and
    // therefore shared by every process in the worker container. A
    // per-device fan-out turns into one `step` job per device, and the
    // worker runs up to Worker:MaxConcurrency (default 10) of them at the
    // same instant. On a host with a modest limit, a handful win the race
    // to allocate a namespace and the rest die with the misleading
    //   bwrap: No permissions to create new namespace, likely because the
    //   kernel does not allow non-privileged user namespaces.
    // The kernel is fine — the namespace table is momentarily full. This
    // is exactly the "some devices succeed, others fail with this exact
    // message in the same run" pattern.
    //
    // Two in-process defenses (neither needs a host change):
    //   1. _sandboxGate caps how many namespace-creating launches run at
    //      once across the whole worker process (Python:MaxConcurrentSandboxes).
    //   2. a setup failure is retried with jittered backoff — by the time
    //      a retry fires, finished siblings have released their namespaces.
    //      Retrying is safe because a setup failure happens BEFORE python3
    //      starts: the script never ran, so there are no side effects and
    //      no idempotency concern (even though the handler defaults to
    //      RequiresCompensation).
    //
    // Operators on a genuinely hardened host (unprivileged userns fully
    // off → EVERY launch fails) still get a clear, actionable error after
    // the retries are exhausted, naming the exact sysctls to turn.
    private const int DefaultMaxConcurrentSandboxes = 4;
    private const int DefaultSandboxSetupRetries = 4;
    // RLIMIT_NPROC cap for the sandbox. Per-UID, so it counts the worker's
    // own threads too — keep it well above the worker's baseline thread
    // count (64 was too low and caused intermittent EAGAIN on clone).
    private const int DefaultMaxProcesses = 1024;

    private static SemaphoreSlim? _sandboxGate;
    private static readonly object _sandboxGateLock = new();

    // Process-wide, lazily sized from Python:MaxConcurrentSandboxes the
    // first time a sandboxed snippet runs. Config is process-global, so the
    // first instance to ask wins and the slot count is stable for the
    // worker's lifetime.
    private SemaphoreSlim SandboxGate()
    {
        if (_sandboxGate is not null) return _sandboxGate;
        lock (_sandboxGateLock)
        {
            if (_sandboxGate is null)
            {
                var max = _configuration.GetValue("Python:MaxConcurrentSandboxes", DefaultMaxConcurrentSandboxes);
                if (max < 1) max = 1;
                _sandboxGate = new SemaphoreSlim(max, max);
            }
        }
        return _sandboxGate;
    }

    // True when the sandbox (bwrap/nsjail) failed to SET UP its namespaces
    // before the wrapped python3 ever ran. The hallmark is a non-zero exit
    // with NO stdout (the script produced nothing) and a stderr that names
    // the namespace / uid-map setup step. This is the only failure we
    // retry, and it is always safe to retry.
    //
    // The empty-stdout guard is the real safety net: a real script that
    // merely printed one of these phrases can never be mistaken for a setup
    // failure and silently re-run.
    internal static bool IsTransientSandboxStartupFailure(int exitCode, string? stdout, string? stderr)
    {
        if (exitCode == 0) return false;
        if (!string.IsNullOrWhiteSpace(stdout)) return false;
        if (string.IsNullOrEmpty(stderr)) return false;

        // A uid/gid map denial is NOT transient and must not be listed here.
        // "bwrap: setting up uid map: Permission denied" means the namespace
        // WAS created and writing /proc/self/uid_map was refused — on Ubuntu
        // 23.10+ that is `kernel.apparmor_restrict_unprivileged_userns=1`, a
        // fixed host policy. Retrying it four times with backoff just delays
        // the same answer; IsSandboxSetupFailure still catches it so the
        // operator gets the actionable message immediately.
        //
        // What remains below are the genuinely contended cases: the namespace
        // could not be ALLOCATED, which a sibling releasing one can fix.
        string[] markers =
        [
            "create new namespace",   // bwrap: No permissions to create new namespace
            "creating new namespace", // bwrap: Creating new namespace failed
            "namespace failed",       // generic "... namespace failed: ..."
            "user namespace",         // "...non-privileged user namespaces", "create user namespace"
            "max_user_namespaces",    // explicit limit text
        ];
        foreach (var m in markers)
            if (stderr.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Broader than IsTransientSandboxStartupFailure: also matches the
    // DETERMINISTIC setup failures we don't retry — bwrap couldn't make a
    // mount inside the sandbox (most often Docker's masked /proc blocking a
    // fresh procfs). Used only to swap the final raw text for an actionable
    // message; same empty-stdout safety guard (the script never ran).
    internal static bool IsSandboxSetupFailure(int exitCode, string? stdout, string? stderr)
    {
        if (exitCode == 0 || !string.IsNullOrWhiteSpace(stdout) || string.IsNullOrEmpty(stderr))
            return false;
        if (IsTransientSandboxStartupFailure(exitCode, stdout, stderr)) return true;

        string[] mountMarkers =
        [
            "can't mount",   // bwrap: Can't mount proc on /newroot/proc
            "/newroot",      // bwrap's internal sandbox root — only in setup errors
            "mount proc",
            "pivot_root",
            // Deterministic host policy, deliberately not in the retry set
            // above — see the note there.
            "setting up uid map",
            "setting up gid map",
        ];
        foreach (var m in mountMarkers)
            if (stderr.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Exponential-ish backoff with per-attempt jitter so concurrent
    // siblings don't all retry on the same tick and re-collide. 1-based
    // attempt → ~100, 200, 400, 800, 1000(+jitter) ms.
    private static int SandboxRetryDelayMs(int attempt)
    {
        var shift = Math.Min(attempt - 1, 4);
        var baseMs = Math.Min(1000, 100 * (1 << shift));
        return baseMs + Random.Shared.Next(0, 100);
    }

    // Actionable replacement for the raw bwrap text on a sandbox-setup
    // failure. The bwrap phrasing / kernel errno disambiguates the cause:
    //   mount /proc EPERM → Docker's masked /proc blocks a fresh procfs.
    //   EAGAIN ("Resource temporarily unavailable") → a process/thread
    //     limit (RLIMIT_NPROC), NOT permissions or namespace count.
    //   EPERM  ("No permissions" / "Operation not permitted") → seccomp or
    //     the kernel forbidding unprivileged user namespaces.
    internal static string SandboxSetupError(string? stderr)
    {
        var tail = Tail(stderr, 300);

        // Mount failure: the kernel refuses a fresh procfs mount when the
        // container's /proc is partially masked (Docker's default). Check
        // first — this error also contains "Operation not permitted" (EPERM),
        // which would otherwise match the seccomp branch below.
        if (!string.IsNullOrEmpty(stderr)
            && stderr.Contains("mount", StringComparison.OrdinalIgnoreCase)
            && (stderr.Contains("proc", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("/newroot", StringComparison.OrdinalIgnoreCase)))
            return
                "python sandbox could not mount /proc inside the namespace: the kernel refuses a "
                + "fresh procfs mount when the container's /proc is partially masked (Docker's "
                + "default). Add `security_opt: - systempaths=unconfined` to the worker service — "
                + "it clears Docker's masked/read-only /proc paths so the sandbox can mount its own "
                + "procfs (or use `privileged: true`). bwrap said: " + tail;

        // uid/gid map denial. Distinct from "couldn't create a namespace": the
        // namespace WAS created and writing /proc/self/uid_map was refused.
        // On Ubuntu 23.10+ that is one specific host policy, and it is worth
        // naming exactly — the generic advice below sends operators to
        // `kernel.unprivileged_userns_clone`, a Debian-era knob that does not
        // exist on those releases, so they find nothing and conclude the
        // message is wrong.
        if (!string.IsNullOrEmpty(stderr)
            && (stderr.Contains("setting up uid map", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("setting up gid map", StringComparison.OrdinalIgnoreCase)))
            return
                "python sandbox was refused a user-namespace id map by the HOST kernel "
                + $"(container '{Environment.MachineName}'). The namespace was created; writing "
                + "/proc/self/uid_map was denied. On Ubuntu 23.10+ / 24.04 this is AppArmor's "
                + "unprivileged-userns restriction, on by default via "
                + "/usr/lib/sysctl.d/10-apparmor.conf. "
                + "Check it on the HOST (not in the container): "
                + "`sysctl kernel.apparmor_restrict_unprivileged_userns` — 1 means this is it. "
                + "Either allow it globally: `echo 'kernel.apparmor_restrict_unprivileged_userns=0' "
                + "| sudo tee /etc/sysctl.d/99-flowweaver-userns.conf && sudo sysctl --system`, "
                + "or keep the hardening and grant just bwrap an AppArmor profile with "
                + "`userns create`. Container-level seccomp/apparmor flags do NOT help here — the "
                + "restriction is enforced on the host and targets exactly the unconfined "
                + "processes that `apparmor=unconfined` produces. "
                + "bwrap said: " + tail;

        // EAGAIN: the clone() for namespace setup hit a resource limit.
        // RLIMIT_NPROC is per-UID, so the worker's own threads count against
        // the sandbox's `--nproc` budget — the usual culprit under concurrency.
        if (!string.IsNullOrEmpty(stderr)
            && (stderr.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("EAGAIN", StringComparison.OrdinalIgnoreCase)))
            return
                "python sandbox could not create a namespace after retries: the kernel returned "
                + "EAGAIN (resource temporarily unavailable) — a process/thread limit, not a "
                + "permissions or namespace-count problem. The prlimit `--nproc` cap is per-UID, "
                + "so it counts the worker's own threads (dozens at rest) plus every concurrent "
                + "sandbox; under load it runs out. Raise `Python:MaxProcesses` (the --nproc cap) "
                + "and/or lower `Python:MaxConcurrentSandboxes` on the worker; if it persists, "
                + "check the host's `kernel.threads-max` / pid limits. bwrap said: " + tail;

        // EPERM: seccomp or the kernel forbidding unprivileged user namespaces.
        // Name the container (hostname) that failed: in a multi-container
        // deploy where only the worker service is seccomp-relaxed, an
        // INTERMITTENT version of this error means a hardened container is
        // claiming sandbox jobs it cannot run — the fix is queue routing
        // (Worker__Tags), not host sysctls.
        return
            $"python sandbox could not create a namespace after retries (container '{Environment.MachineName}') "
            + "— this is a host/container "
            + "limit, not the script (bwrap fails before the script runs). "
            + "If it fails only SOMETIMES, a container WITHOUT the seccomp/apparmor relaxations is "
            + "claiming python steps: make sure only the relaxed worker service claims the `sandbox` "
            + "queue tag (compare this error's container name against `docker ps`, and check the "
            + "Worker__Tags overrides in deploy/docker-compose.yml). "
            + "Most common in Docker: the default seccomp profile blocks the non-root worker "
            + "from creating user namespaces even when the host kernel allows them — add "
            + "`security_opt: [seccomp=unconfined, apparmor=unconfined]` (or `cap_add: [SYS_ADMIN]`) "
            + "to the worker service. Confirm with `docker exec -u app <worker> bwrap --ro-bind / / "
            + "--unshare-user true`: if that fails in the container but the host has "
            + "unprivileged userns on, it's seccomp. "
            + "If a bare bwrap also fails on the HOST, unprivileged user namespaces are off at the "
            + "kernel. The knob differs by distro and getting this wrong wastes an afternoon: "
            + "Ubuntu 23.10+/24.04 use `kernel.apparmor_restrict_unprivileged_userns` (set it to 0); "
            + "older Debian/Ubuntu use `kernel.unprivileged_userns_clone` (set it to 1); "
            + "RHEL and derivatives use `user.max_user_namespaces` (set it to 15000). "
            + "Check which one your host actually has with "
            + "`sysctl -a 2>/dev/null | grep -E 'userns|user_namespaces'`. "
            + "bwrap said: " + tail;
    }

    // On Unix a process killed by signal N is reported as exit code 128+N, and
    // bwrap propagates its child's status the same way. 64 is the highest real
    // signal number, so anything above 192 is a genuine exit code that merely
    // looks like one — a script may legitimately `sys.exit(200)`.
    internal static bool IsSignalDeath(int exitCode, out int signal)
    {
        signal = exitCode - 128;
        return exitCode > 128 && signal <= 64;
    }

    private static string SignalName(int signal) => signal switch
    {
        2 => "SIGINT",
        6 => "SIGABRT",
        9 => "SIGKILL",
        11 => "SIGSEGV",
        15 => "SIGTERM",
        24 => "SIGXCPU",
        25 => "SIGXFSZ",
        _ => $"signal {signal}",
    };

    // Turns a signal death into something an operator can act on. The
    // per-signal branches map to the specific knobs this handler sets, because
    // three of them (SIGXCPU, SIGXFSZ, SIGSEGV) are self-inflicted by our own
    // prlimit wrapper and are fixed by changing config, while SIGKILL is not.
    internal static string SignalDeathError(
        int signal, TimeSpan elapsed, int timeoutSec, bool oomKilled, bool oomCounterReadable)
    {
        // Invariant, not current-culture: the worker's locale decides whether
        // `F1` prints "55.9" or "55,9", and an operator-facing diagnostic that
        // changes shape with the host's locale is one that can't be grepped or
        // matched on.
        var seconds = elapsed.TotalSeconds.ToString(
            "F1", System.Globalization.CultureInfo.InvariantCulture);
        var ran = $"after {seconds}s (timeout was {timeoutSec}s)";
        var name = SignalName(signal);

        return signal switch
        {
            // Our own RLIMIT_CPU (prlimit --cpu = the step timeout). A script
            // that BURNS cpu for longer than its wall-clock timeout allows
            // trips this before the timeout does — most often a busy-wait loop
            // where the author meant to sleep.
            24 =>
                $"python sandbox killed by {name} {ran}: the script used more CPU time than its "
                + $"timeout allows ({timeoutSec}s of CPU). This is a busy loop, not a slow call — "
                + "a script that sleeps or waits on I/O uses almost no CPU. Use time.sleep() "
                + "instead of a spin loop, or raise the snippet's timeout_seconds.",

            // Our own RLIMIT_FSIZE.
            25 =>
                $"python sandbox killed by {name} {ran}: the script tried to write more than 10 MiB "
                + "to a file. Snippets return data through set_output(), not by writing files — "
                + "the sandbox's /tmp is a private tmpfs that is discarded when the step ends.",

            11 =>
                $"python sandbox crashed with {name} (segmentation fault) {ran}. This is an "
                + "interpreter or native-extension crash, not a Python exception. If the snippet "
                + "imports a compiled package, suspect that package; if it does not, raise "
                + "Python:MaxMemoryMb (the prlimit --as cap, default 256 MB) in case the crash is "
                + "an allocation failure the interpreter could not report.",

            9 when oomKilled =>
                $"python sandbox OOM-killed by the kernel {ran}: the cgroup's oom_kill counter "
                + "increased while this step ran, so the container hit its memory limit. Raise the "
                + "worker container's memory limit, lower Python:MaxConcurrentSandboxes so fewer "
                + "sandboxes run at once, or reduce what the script holds in memory.",

            9 =>
                $"python sandbox killed by {name} {ran}. This did NOT come from FlowWeaver: the "
                + "step timeout was not reached, and the handler's own limits raise different "
                + "signals (SIGXCPU for CPU, SIGXFSZ for file size). "
                + (oomCounterReadable
                    ? "The kernel's OOM-kill counter for this cgroup did NOT increase, so it was "
                      + "not an out-of-memory kill either — something outside the container sent "
                      + "SIGKILL. "
                    : "The kernel's OOM-kill counter could not be read, so an out-of-memory kill "
                      + "cannot be ruled out here. ")
                + "Check, on the worker's host: `dmesg -T | tail -50` for an OOM or audit record "
                + "around the failure time, `docker events --since 10m` for an external stop/kill, "
                + "and any security agent (auditd, falco, SELinux/AppArmor enforcement, EDR) that "
                + "may be terminating long-lived `bwrap` processes.",

            _ =>
                $"python sandbox killed by {name} {ran}. The script produced no output because the "
                + "signal was not catchable. Check the worker host's logs for what sent it.",
        };
    }

    private static string Tail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }

    // One launch of the (already-resolved) sandbox command. Returns the
    // captured streams plus a TimedOut flag so the caller can tell a
    // wall-clock kill apart from a normal non-zero exit. Pure mechanics —
    // the retry/throttle policy lives in the caller.
    private static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunProcessOnceAsync(
        string fileName, IReadOnlyList<string> argv, string inputJson, int timeoutSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Drain stdout/stderr from the start so the child can never block on
        // a full output pipe while we're still feeding its stdin.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        // Feed the JSON payload to stdin. If the sandbox failed to start
        // (a bwrap setup error → the wrapped python never runs, so nothing
        // reads stdin), the pipe's read end is already closed and the write
        // throws EPIPE ("Broken pipe"). That is NOT our failure: swallow it
        // and fall through to read the child's stderr + exit code, which
        // carry the real reason (e.g. "Can't mount proc …"). Without this,
        // a fast-failing sandbox surfaces as an opaque "Broken pipe" instead
        // of the actionable SandboxSetupError. Only a genuine cancellation
        // (shutdown/timeout) should propagate — handled by WaitForExit below.
        try
        {
            await proc.StandardInput.WriteAsync(inputJson.AsMemory(), cts.Token);
            proc.StandardInput.Close();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { /* timeout — caught below */ }
        catch (IOException) { /* broken pipe: child closed stdin / already exited */ }
        catch (ObjectDisposedException) { /* stdin already torn down */ }

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, string.Empty, string.Empty, true);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr, false);
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new SnippetResult
            {
                // Refused before the script ran, so nothing happened.
                Change = StepChange.Unchanged,
                Success = false,
                Error = "python_snippet handler only runs on Linux (container deployment)",
            };
        }

        // Refuse to execute untrusted Python without a sandbox outside
        // Development. The static allow-list is a defense, not the only
        // defense; operator-supplied scripts must run under bwrap/nsjail.
        var sandboxModeRaw = (_configuration["Python:SandboxMode"] ?? string.Empty).Trim().ToLowerInvariant();
        var hasCustom = !string.IsNullOrWhiteSpace(_configuration["Python:SandboxCommand"]);
        var sandboxOff = sandboxModeRaw == "none" && !hasCustom;
        if (sandboxOff && !_env.IsDevelopment())
        {
            return new SnippetResult
            {
                // Refused before the script ran, so nothing happened.
                Change = StepChange.Unchanged,
                Success = false,
                Error = "python_snippet sandbox disabled outside Development. Set Python:SandboxMode to 'bwrap' or 'nsjail', or supply Python:SandboxCommand.",
            };
        }

        var input = request.InputPayload;

        // Priority: snippet first, then legacy inline overrides.
        // This matches how users configure the snippet in the UI — the
        // script body is edited on the snippet row, not the node payload.
        string? script = request.SnippetCode;
        if (string.IsNullOrWhiteSpace(script)
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("script", out var s))
            script = s.GetString();
        if (string.IsNullOrWhiteSpace(script)
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("code", out var c))
            script = c.GetString();
        if (string.IsNullOrWhiteSpace(script))
            return Fail("snippet code is empty — set the script on the snippet");

        // Static safety check — reject scripts that import outside the
        // allow-list or reach for eval/exec/open/interpreter internals. Allowed
        // modules = the static safe-list plus the admin-approved (and
        // installed) extra imports.
        //
        // Two passes, because the cheap one is not sufficient on its own:
        //   1. CheckDangerousCode — C#, no process spawn, catches the obvious.
        //   2. flow_weaver_import_guard.py — `ast.parse` against the real
        //      grammar. This is the authoritative pass; the C# one cannot see
        //      `import\tos`, `if 1: import os` or `getattr(x, "ev"+"al")`.
        var extraAllowed = await LoadAllowedModulesAsync(ct);
        var dangerousError = CheckDangerousCode(script, request.NetworkEnabled, extraAllowed);
        if (dangerousError is not null)
            return Fail(dangerousError);

        var guardError = await RunImportGuardAsync(script, request.NetworkEnabled, extraAllowed, ct);
        if (guardError is not null)
            return Fail(guardError);

        // Network-enabled snippets run with the sandbox's network isolation
        // lifted (admin-gated at create time). Log it so the escalation is
        // visible in the worker audit trail.
        if (request.NetworkEnabled)
            _logger.LogInformation(
                "worker.python.network_enabled step_run_id={StepRunId} snippet_id={SnippetId} device_id={DeviceId}",
                request.StepRunId, request.SnippetId, request.DeviceId);

        // Resolution: explicit input override > snippet setting > 60s default.
        var timeoutSec = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("timeout_seconds", out var t)
            && t.TryGetInt32(out var tv)
                ? tv
                : request.SnippetTimeoutSeconds > 0
                    ? request.SnippetTimeoutSeconds
                    : 60;
        timeoutSec = Math.Clamp(timeoutSec, 5, 600);

        _logger.LogDebug(
            "worker.python.start step_run_id={StepRunId} device_id={DeviceId} timeout_seconds={TimeoutSeconds} script_bytes={ScriptBytes}",
            request.StepRunId, request.DeviceId, timeoutSec, script.Length);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"fw-{Guid.NewGuid():N}.py");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await File.WriteAllTextAsync(tmpFile, script, ct);

            var rawInput = input.TryGetProperty("input", out var inEl)
                ? inEl
                : input;

            // Resolve any ${secret:...} placeholders in the payload. This
            // happens BEFORE we enumerate integration ids so even the ids
            // themselves can come from a secret source. Strings that
            // don't contain a marker are a no-op.
            var effectiveInput = await ResolveSecretsAsync(rawInput, ct);

            // Augment the stdin payload with a pre-resolved `_integrations`
            // dict so the runtime helper has base_url + headers ready to go.
            // We keep the original payload shape intact (everything the
            // script wrote in config_overrides still arrives) and simply
            // add the new key.
            var integrations = await ResolveIntegrationsAsync(effectiveInput, ct);
            var inputJson = EmbedIntegrations(effectiveInput, integrations);

            var pkgDir = ResolvePackagesDir();
            var (mode, fileName, argv) = ResolvePythonInvocation(tmpFile, timeoutSec, request.NetworkEnabled, pkgDir);

            // Only namespace-creating sandboxes are subject to the launch
            // throttle + transient-setup retry. Bare python3 ("none",
            // Development only) creates no namespaces, so it needs neither.
            var usesSandbox = mode is "bwrap" or "nsjail" or "custom";
            var maxAttempts = usesSandbox
                ? Math.Max(1, _configuration.GetValue("Python:SandboxSetupRetries", DefaultSandboxSetupRetries) + 1)
                : 1;

            var stdout = string.Empty;
            var stderr = string.Empty;
            var exitCode = -1;

            // Sampled around the run so a signal death can be attributed. See
            // CgroupOom: a kernel OOM kill and an external `kill -9` are
            // indistinguishable from the exit code alone.
            var oomBefore = CgroupOom.ReadKillCount();

            for (var attempt = 1; ; attempt++)
            {
                // Throttle simultaneous namespace creation across the whole
                // worker process so a per-device fan-out can't fire N bwrap
                // launches at the same instant and exhaust the host's
                // user.max_user_namespaces table.
                if (usesSandbox) await SandboxGate().WaitAsync(ct);
                bool timedOut;
                try
                {
                    (exitCode, stdout, stderr, timedOut) =
                        await RunProcessOnceAsync(fileName, argv, inputJson, timeoutSec, ct);
                }
                finally
                {
                    // Release before any backoff sleep so a sibling can take
                    // the slot while this attempt waits to retry.
                    if (usesSandbox) SandboxGate().Release();
                }

                if (timedOut)
                {
                    sw.Stop();
                    _logger.LogError(
                        "worker.python.failed step_run_id={StepRunId} device_id={DeviceId} reason={Reason} duration_ms={DurationMs}",
                        request.StepRunId, request.DeviceId, "timeout", sw.ElapsedMilliseconds);
                    return Fail($"python process timed out after {timeoutSec}s");
                }

                // Retry ONLY a sandbox-setup failure — the wrapped python3
                // never started, so the script had no chance to run and
                // re-running is free of side effects. A real script error
                // (non-empty stdout, a Python traceback, a non-namespace
                // stderr) is returned untouched.
                if (usesSandbox
                    && attempt < maxAttempts
                    && IsTransientSandboxStartupFailure(exitCode, stdout, stderr))
                {
                    var delayMs = SandboxRetryDelayMs(attempt);
                    _logger.LogWarning(
                        "worker.python.sandbox_retry step_run_id={StepRunId} device_id={DeviceId} attempt={Attempt} max_attempts={Max} delay_ms={DelayMs} stderr_tail={StderrTail}",
                        request.StepRunId, request.DeviceId, attempt, maxAttempts, delayMs, Tail(stderr, 200));
                    try { await Task.Delay(delayMs, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                break;
            }

            sw.Stop();

            // A sandbox-SETUP failure (bwrap couldn't build the sandbox — a
            // namespace it couldn't create, or a mount it couldn't make) is a
            // host/container limit, not a script bug. Swap the raw bwrap text
            // for an actionable message naming the exact knob to turn. This is
            // broader than the retry gate above: deterministic mount failures
            // (e.g. Docker's masked /proc) aren't retried but still get the
            // actionable message here.
            if (usesSandbox && exitCode != 0
                && IsSandboxSetupFailure(exitCode, stdout, stderr))
            {
                _logger.LogError(
                    "worker.python.failed step_run_id={StepRunId} device_id={DeviceId} reason={Reason} duration_ms={DurationMs}",
                    request.StepRunId, request.DeviceId, "sandbox_setup", sw.ElapsedMilliseconds);
                return Fail(SandboxSetupError(stderr));
            }

            // Killed by a signal. Before this, every one of these surfaced as
            // the bare string "exit code 137:" with empty logs — the script
            // produced no output because SIGKILL cannot be caught and
            // flowweaver_runtime flushes from an `atexit` hook that never ran.
            // That message named neither the signal nor anything to check, so
            // the same failure recurred without ever being diagnosed.
            if (IsSignalDeath(exitCode, out var signal))
            {
                var oomAfter = CgroupOom.ReadKillCount();
                var oomKilled = oomBefore is { } b && oomAfter is { } a && a > b;

                _logger.LogError(
                    "worker.python.failed step_run_id={StepRunId} device_id={DeviceId} reason={Reason} "
                    + "signal={Signal} duration_ms={DurationMs} timeout_seconds={TimeoutSeconds} "
                    + "cgroup_oom_kills={OomBefore}->{OomAfter} argv={FileName} {Argv}",
                    request.StepRunId, request.DeviceId, "signal", signal, sw.ElapsedMilliseconds,
                    timeoutSec, oomBefore, oomAfter, fileName, string.Join(' ', argv));

                return Fail(SignalDeathError(signal, sw.Elapsed, timeoutSec, oomKilled, oomAfter is not null));
            }

            // Try parsing stdout as JSON for structured output; fall back
            // to raw string.
            JsonElement outputEl;
            try
            {
                outputEl = JsonDocument.Parse(stdout).RootElement;
            }
            catch
            {
                outputEl = JsonSerializer.SerializeToElement(new { raw = stdout.TrimEnd() });
            }

            // Extract the structured log buffer the runtime flushes on
            // atexit and merge it into the output so downstream nodes
            // (and the monitor UI) can read the entries as JSON. The
            // human-readable stderr tail still lives in Logs for quick
            // eyeballing in docker logs.
            var (structuredLogs, cleanStderr) = ExtractStructuredLogs(stderr);
            if (structuredLogs is not null)
                outputEl = AttachLogs(outputEl, structuredLogs.Value);

            var success = exitCode == 0;
            if (success)
            {
                _logger.LogInformation(
                    "worker.python.ok step_run_id={StepRunId} device_id={DeviceId} exit_code={ExitCode} duration_ms={DurationMs} stdout_bytes={StdoutBytes} stderr_bytes={StderrBytes}",
                    request.StepRunId, request.DeviceId, exitCode, sw.ElapsedMilliseconds, stdout?.Length ?? 0, cleanStderr?.Length ?? 0);
            }
            else
            {
                _logger.LogError(
                    "worker.python.failed step_run_id={StepRunId} device_id={DeviceId} exit_code={ExitCode} duration_ms={DurationMs} stdout_bytes={StdoutBytes} stderr_bytes={StderrBytes}",
                    request.StepRunId, request.DeviceId, exitCode, sw.ElapsedMilliseconds, stdout?.Length ?? 0, cleanStderr?.Length ?? 0);
            }

            return new SnippetResult
            {
                Success = success,
                Output = outputEl,
                // An author's script. It may have posted to an API or printed a number,
                // and this handler sees the same exit code either way — so the author
                // declares, on the snippet that carries the code.
                Change = StepChange.AuthorDecides,
                Logs = $"exit={exitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{cleanStderr}",
                Error = success ? string.Empty : $"exit code {exitCode}: {cleanStderr}".Trim(),
            };
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* cleanup best effort */ }
        }
    }

    private const string DefaultImportGuardPath = "/usr/local/lib/flow_weaver_import_guard.py";

    // Authoritative static check: runs flow_weaver_import_guard.py, which
    // `ast.parse`s the snippet and walks the tree for disallowed imports and
    // for eval/exec/open/getattr/interpreter internals. Returns an error
    // message when the script must be refused, or null when it passes.
    //
    // The guard never executes the snippet — `ast.parse` only builds a tree —
    // so running it outside the sandbox on hostile input is safe, and doing it
    // before the sandbox means a rejected script costs no namespace setup.
    //
    // FAILS CLOSED. If the guard is missing, times out, or answers something we
    // can't parse, the snippet is refused outside Development. It is the only
    // thing standing between an arbitrary snippet and `import os` inside a
    // sandbox that, for network_enabled snippets, has the host network — so
    // "we couldn't check" must never mean "run it anyway". Development keeps
    // the C# pre-filter alone so local iteration doesn't need the worker image.
    private async Task<string?> RunImportGuardAsync(
        string script, bool networkEnabled, IReadOnlySet<string> extraAllowed, CancellationToken ct)
    {
        var guardPath = _configuration["Python:ImportGuardPath"];
        if (string.IsNullOrWhiteSpace(guardPath)) guardPath = DefaultImportGuardPath;

        if (!File.Exists(guardPath))
        {
            if (_env.IsDevelopment())
            {
                _logger.LogWarning(
                    "worker.python.import_guard_missing path={Path} — running with the C# pre-filter only "
                    + "(Development). That filter does NOT catch `import<tab>os` or getattr-based access.",
                    guardPath);
                return null;
            }

            return
                $"python_snippet import guard not found at '{guardPath}'. Refusing to run an "
                + "unverified script. Ship deploy/python/flow_weaver_import_guard.py in the worker "
                + "image (see deploy/Dockerfile) or point Python:ImportGuardPath at it.";
        }

        var allowed = (networkEnabled ? NetworkSafeModules : SafeModules)
            .Concat(extraAllowed)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var payload = JsonSerializer.Serialize(new { script, allowed });

        var exe = _configuration["Python:Executable"];
        if (string.IsNullOrWhiteSpace(exe)) exe = "python3";

        try
        {
            // 20s is generous for an ast.parse; the cap exists so a
            // pathological script (deep nesting) can't wedge a worker slot.
            var (exitCode, stdout, stderr, timedOut) =
                await RunProcessOnceAsync(exe, new[] { guardPath }, payload, 20, ct);

            if (timedOut)
                return "python_snippet import guard timed out while parsing the script — refusing to run it.";

            if (exitCode != 0)
            {
                _logger.LogError(
                    "worker.python.import_guard_failed exit_code={ExitCode} stderr_tail={StderrTail}",
                    exitCode, Tail(stderr, 300));
                return "python_snippet import guard failed to run — refusing to run an unverified script.";
            }

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("ok", out var okEl)
                && okEl.ValueKind == JsonValueKind.True)
                return null;

            var reason = doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : "rejected by the import guard";
            return $"Blocked: {reason}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "worker.python.import_guard_error path={Path}", guardPath);
            return "python_snippet import guard errored — refusing to run an unverified script.";
        }
    }

    // Safe modules that are explicitly allowed in sandboxed scripts.
    //
    // Hard rule for additions: stdlib-only, no I/O, no network, no
    // subprocess, no OS introspection. `flowweaver_runtime` is the
    // stdin/stdout bridge shipped in the worker image — snippets need
    // it to call get_input() / set_output() / integration(name).
    //
    // Additions 2026-Q2 (LLDP/DNS reconciliation incident):
    //   - csv, io, base64 → so snippets can build a CSV and encode it
    //     base64 for email attachments. Before this, any workflow
    //     that needed a dynamic-row CSV couldn't be implemented in
    //     python_snippet, which pushed the agent toward scaffold/report
    //     hacks that landed placeholder data in production reports.
    //   - string, collections, itertools → the usual suspects for
    //     tabular transforms (Counter, groupby, chain). All stdlib.
    //   - hashlib → needed whenever a snippet has to identify a row by
    //     content hash (dedup, idempotency keys). Pure compute.
    //
    // Deliberately NOT added: `urllib.*`, `socket`, `subprocess`, `os`,
    // `pathlib`, `requests`, `httpx`. Anything network- or OS-adjacent
    // has to go through `integration(name)` from flowweaver_runtime so
    // the worker can apply credentials, URL guards, and audit logs.
    private static readonly HashSet<string> SafeModules = new(StringComparer.Ordinal)
    {
        "json", "math", "datetime", "re", "ipaddress", "flowweaver_runtime",
        "csv", "io", "base64", "string", "collections", "itertools",
        "hashlib", "time",
    };

    // Extended allowlist for NetworkEnabled snippets ONLY (admin-gated). Adds the
    // network-driving libraries an interactive SSH snippet needs on top of the
    // base safe set. The exec/eval/open/__import__ bans still apply — this only
    // relaxes which modules may be imported, paired with the relaxed sandbox
    // (host network + DNS) in BuildBwrap.
    private static readonly HashSet<string> NetworkSafeModules = new(SafeModules, StringComparer.Ordinal)
    {
        "netmiko", "paramiko", "socket", "select", "textfsm",
        "ntc_templates", "logging",
    };

    /// <summary>
    /// Cheap pre-filter over the snippet source. Returns an error message when
    /// the script is obviously unsafe, or null when it passes.
    /// </summary>
    /// <remarks>
    /// NOT the authoritative check — <see cref="RunImportGuardAsync"/> is, and
    /// it runs on every snippet right after this one. This pass matches string
    /// shapes against source text, which Python's grammar does not respect:
    /// `import\tos`, `if 1: import os`, `import (os)` and
    /// `getattr(x, "ev" + "al")` all sail through here. It is kept because it
    /// costs nothing, rejects the common mistake without a process spawn, and
    /// gives a friendlier message for the 99% case.
    ///
    /// Do not "harden" this by adding more substring patterns. If something
    /// gets past it, the fix belongs in flow_weaver_import_guard.py, which
    /// parses the real grammar.
    /// </remarks>
    // internal for unit tests (InternalsVisibleTo).
    //
    // `extraAllowed` is the admin-approved import allow-list (the
    // `ready` rows of AllowedPythonModule). It only ADDS importable modules; the
    // exec/eval/__import__/open bans below still apply to every script. Null/empty
    // keeps the legacy static-only behavior (used by the unit tests).
    internal static string? CheckDangerousCode(
        string script, bool networkEnabled, IReadOnlySet<string>? extraAllowed = null)
    {
        // Network-enabled (admin-gated) snippets get the extended module set.
        var allowed = networkEnabled ? NetworkSafeModules : SafeModules;

        // Check dangerous built-in calls (always blocked, even network-enabled).
        string[] dangerousCalls = ["exec(", "eval(", "__import__", "open("];
        foreach (var call in dangerousCalls)
        {
            if (script.Contains(call, StringComparison.Ordinal))
                return $"Blocked: script contains disallowed call '{call.TrimEnd('(')}'";
        }

        // Walk statement-by-statement. Statement boundaries are newlines, `;`
        // and `:` — the last one so `if cond: import os` is seen as an import
        // rather than skipped as "some statement starting with if". The keyword
        // is then matched against any whitespace run, not a literal space, so a
        // tab or a double space doesn't hide the import.
        // Shapes handled, each validated only against the top-level module:
        //   import X                      → X
        //   import X, Y                   → X, Y
        //   import X as Z                 → X
        //   import X.Y                    → X
        //   from X import a, b            → X  (a/b are names, not modules)
        //   from X.Y import a             → X
        // Parenthesised and backslash-continued imports still fall through —
        // by design, see the remarks above: the AST guard is what catches them.
        foreach (var statement in SplitStatements(script))
        {
            var stripped = statement.TrimStart();
            string? topModule = null;

            if (TryStripKeyword(stripped, "from", out var fromRest))
            {
                // from X[.Y] import ...  — only X matters for the allowlist.
                var rest = fromRest.AsSpan();
                var spaceIdx = rest.IndexOfAny(' ', '\t');
                if (spaceIdx > 0)
                    topModule = rest[..spaceIdx].ToString();
            }
            else if (TryStripKeyword(stripped, "import", out var importRest))
            {
                // import X[, Y, Z]  — validate every comma-separated name.
                var rest = importRest;
                foreach (var raw in rest.Split(','))
                {
                    var token = raw.Trim();
                    if (token.Length == 0) continue;

                    // Drop "as <alias>" suffix if present.
                    var asIdx = token.IndexOf(" as ", StringComparison.Ordinal);
                    if (asIdx > 0) token = token[..asIdx].TrimEnd();

                    var error = CheckModuleAllowed(token, allowed, extraAllowed);
                    if (error is not null) return error;
                }
                continue;
            }

            if (topModule is null) continue;

            var err = CheckModuleAllowed(topModule, allowed, extraAllowed);
            if (err is not null) return err;
        }

        return null;
    }

    // Splits a script into statement-like fragments. Newlines, `;` and `:` are
    // all treated as boundaries — `:` because a compound header hides the real
    // statement behind it (`if 1: import os`, `else: import socket`), and
    // splitting on it surfaces the tail as its own fragment.
    //
    // Splitting on `:` also chops dict literals, slices, annotations and
    // lambdas into fragments. That's harmless here: a fragment only matters if
    // it parses as an import, and the resulting false fragments don't.
    private static IEnumerable<string> SplitStatements(string script)
    {
        foreach (var line in script.Split('\n'))
        {
            foreach (var stmt in line.Split(';', ':'))
            {
                if (stmt.Length > 0) yield return stmt;
            }
        }
    }

    // Matches `keyword` followed by ANY whitespace run, returning what follows
    // with leading whitespace trimmed. The literal-space version this replaces
    // ("import ") missed `import\tos` entirely — a tab is valid Python and the
    // statement went completely unchecked.
    private static bool TryStripKeyword(string statement, string keyword, out string rest)
    {
        rest = string.Empty;
        if (!statement.StartsWith(keyword, StringComparison.Ordinal)) return false;

        var i = keyword.Length;
        // There must be at least one whitespace char, or this is an identifier
        // that merely starts with the keyword (`imported = 1`).
        if (i >= statement.Length || !char.IsWhiteSpace(statement[i])) return false;

        while (i < statement.Length && char.IsWhiteSpace(statement[i])) i++;
        rest = statement[i..];
        return true;
    }

    private static string? CheckModuleAllowed(
        string module, HashSet<string> allowed, IReadOnlySet<string>? extra)
    {
        // Strip the dotted tail so `os.path` → `os`, matching Python's
        // top-level package check.
        var dotIdx = module.IndexOf('.');
        var top = dotIdx > 0 ? module[..dotIdx] : module;
        if (string.IsNullOrWhiteSpace(top)) return null;
        if (allowed.Contains(top)) return null;
        if (extra is not null && extra.Contains(top)) return null;
        return $"Blocked: script imports disallowed module '{top}'";
    }

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };

    // ─────────────────────────────────────────────────────────────────
    //  Integration injection
    // ─────────────────────────────────────────────────────────────────

    // Scans the script's input for any `<name>_integration_id` properties
    // (convention used by the agent + skill doc) and loads the matching
    // Integration row. The returned map is name → rendered config that
    // the flowweaver_runtime.integration() helper consumes.
    //
    // The value of each `<name>_integration_id` property can be:
    //   1. A UUID — preferred, unambiguous.
    //   2. A display name or slug (case-insensitive) — tolerated because
    //      agent-generated workflows (and humans hand-editing JSON) often
    //      drop the plain name "netbox" in there instead of copying the
    //      UUID. We look up by `Integration.Name` as a fallback.
    //
    // When the value matches neither, we emit a WARN log so the failure
    // surfaces in the worker logs instead of silently handing the script
    // an empty `_integrations` pool (which then produces the confusing
    // `integration 'netbox' not available — known: <none>` error at
    // runtime).
    private async Task<Dictionary<string, RenderedIntegration>> ResolveIntegrationsAsync(
        JsonElement payload, CancellationToken ct)
    {
        var resolved = new Dictionary<string, RenderedIntegration>(StringComparer.OrdinalIgnoreCase);
        if (payload.ValueKind != JsonValueKind.Object) return resolved;

        foreach (var prop in payload.EnumerateObject())
        {
            if (!prop.Name.EndsWith("_integration_id", StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind != JsonValueKind.String) continue;

            var rawValue = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(rawValue)) continue;

            // `netbox_integration_id` → `netbox`; `infoblox_integration_id`
            // → `infoblox`. Keys are lowercased so the Python helper can
            // call integration("netbox") regardless of how the key was
            // written in config_overrides.
            var handle = prop.Name[..^"_integration_id".Length].ToLowerInvariant();
            if (string.IsNullOrEmpty(handle) || resolved.ContainsKey(handle)) continue;

            var integration = await LookupIntegrationAsync(rawValue, ct);
            if (integration is null)
            {
                _logger.LogWarning(
                    "worker.python.integration_not_found handle={Handle} value={Value} — the script's integration('{Handle}') call will fail. Set '{Key}' to a valid integration UUID (or an exact active integration name) in config_overrides.",
                    handle, rawValue, handle, prop.Name);
                continue;
            }

            try
            {
                resolved[handle] = await RenderAsync(integration, ct);
            }
            catch (InvalidOperationException ex)
            {
                // oauth2_client_credentials grant failed — registering the
                // integration anyway would hand the script headers with no
                // Authorization and a baffling upstream 401. Skip it and say
                // why in the worker log; the script sees a clean
                // "integration '<handle>' not available".
                _logger.LogWarning(
                    "worker.python.integration_auth_failed handle={Handle} integration_id={IntegrationId} error={Error}",
                    handle, integration.IntegrationId, ex.Message);
            }
        }

        return resolved;
    }

    // Looks up an active integration.
    // Accepts either a UUID or a name (case-insensitive). Returns null
    // when nothing matches so the caller can decide how to surface the
    // miss — we don't throw because the script may still run without
    // the integration (e.g. if the author mistyped one handle but others
    // are fine).
    private async Task<IntegrationModel?> LookupIntegrationAsync(
        string value, CancellationToken ct)
    {
        if (Guid.TryParse(value, out var integrationId))
        {
            return await _integrations.GetByIdAsync(integrationId, tracking: false, ct: ct);
        }

        // Name fallback. `Name` is not guaranteed to be unique, so the repo
        // matches the first active row case-insensitively (UI prevents
        // duplicate names at create time, but legacy rows may still collide).
        return await _integrations.FindActiveByNameAsync(value, ct);
    }

    // Produces a dictionary of headers (auth + custom) that the Python
    // runtime will send on every request through this integration. We
    // run a throwaway HttpRequestMessage through the async auth applier
    // so the rules stay in one place — whatever works for the other
    // handlers also works here, including an oauth2_client_credentials
    // Bearer token obtained (and cached) at render time. The token's
    // remaining lifetime bounds the script: the snippet's own timeout
    // (max 600s) is far below any sane access-token TTL.
    private async Task<RenderedIntegration> RenderAsync(IntegrationModel integration, CancellationToken ct)
    {
        using var dummy = new HttpRequestMessage();
        await _authApplier.ApplyAsync(dummy, integration, ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in dummy.Headers)
            headers[h.Key] = string.Join(", ", h.Value);

        return new RenderedIntegration(
            integration.BaseURL?.TrimEnd('/') ?? string.Empty,
            headers,
            integration.TLSSkipVerify);
    }

    // Stream-rewrites the input JsonElement adding the `_integrations`
    // property. We avoid Dictionary round-trips so nested structures
    // (like `document` in a report config) stay intact without a
    // case-sensitive DeepClone pass.
    private static string EmbedIntegrations(
        JsonElement payload,
        Dictionary<string, RenderedIntegration> integrations)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in payload.EnumerateObject())
                {
                    if (prop.NameEquals("_integrations")) continue;
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }

            writer.WritePropertyName("_integrations");
            writer.WriteStartObject();
            foreach (var (name, cfg) in integrations)
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteString("base_url", cfg.BaseUrl);
                writer.WriteBoolean("tls_skip_verify", cfg.TlsSkipVerify);
                writer.WritePropertyName("headers");
                writer.WriteStartObject();
                foreach (var (k, v) in cfg.Headers)
                    writer.WriteString(k, v);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed record RenderedIntegration(
        string BaseUrl,
        Dictionary<string, string> Headers,
        bool TlsSkipVerify);

    // ─────────────────────────────────────────────────────────────────
    //  Structured log extraction
    // ─────────────────────────────────────────────────────────────────

    private const string LogMarker = "__fw_logs__";

    // The runtime flushes a single line of the form
    //   __fw_logs__[{"level":...}, ...]
    // at process exit. Everything before the marker is regular stderr
    // (print(), traceback, etc.) and should stay in Logs verbatim so the
    // user can eyeball it. We split once on the last occurrence so a
    // stray `__fw_logs__` inside a print() output doesn't confuse the
    // parser.
    private static (JsonElement? Logs, string Stderr) ExtractStructuredLogs(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return (null, stderr);
        var idx = stderr.LastIndexOf(LogMarker, StringComparison.Ordinal);
        if (idx < 0) return (null, stderr);

        var tail = stderr[(idx + LogMarker.Length)..].Trim();
        if (tail.Length == 0) return (null, stderr);

        try
        {
            var doc = JsonDocument.Parse(tail);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (null, stderr);
            var cleaned = stderr[..idx].TrimEnd('\n', '\r');
            return (doc.RootElement, cleaned);
        }
        catch (JsonException)
        {
            return (null, stderr);
        }
    }

    // Merges a `_logs` field into the output object. When the script's
    // stdout wasn't an object (script returned a number, an array, or
    // raw text), we wrap it as `{ "value": <original>, "_logs": [...] }`
    // so downstream templates can still reference `steps.X.output._logs`
    // without having to care about the shape.
    private static JsonElement AttachLogs(JsonElement output, JsonElement logs)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            if (output.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var prop in output.EnumerateObject())
                {
                    if (prop.NameEquals("_logs")) continue;
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
                writer.WritePropertyName("_logs");
                logs.WriteTo(writer);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                output.WriteTo(writer);
                writer.WritePropertyName("_logs");
                logs.WriteTo(writer);
                writer.WriteEndObject();
            }
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Secret resolution
    // ─────────────────────────────────────────────────────────────────

    // Walks the payload tree and replaces any string that looks like a
    // `${secret:...}` template with the resolved plaintext. Non-string
    // values pass through unchanged. The round-trip preserves object
    // key order, array ordering, and nested shape — only string leaves
    // are rewritten.
    //
    // This is deliberately done ABOVE the integration dict assembly so
    // admins can store an integration id under a Secret row and reference
    // it from a snippet's config_overrides as
    //   "netbox_integration_id": "${secret:secret:netbox_integration_id:value}"
    // without the handler choking on the literal string.
    private async Task<JsonElement> ResolveSecretsAsync(
        JsonElement input, CancellationToken ct)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return input;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            await WriteWithSecretsAsync(writer, input, ct);
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    private async Task WriteWithSecretsAsync(
        Utf8JsonWriter writer, JsonElement el, CancellationToken ct)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var raw = el.GetString() ?? string.Empty;
                var resolved = raw.Contains("${secret:", StringComparison.Ordinal)
                    ? await _secrets.SubstituteAsync(raw, ct)
                    : raw;
                writer.WriteStringValue(resolved);
                break;

            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    await WriteWithSecretsAsync(writer, prop.Value, ct);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                    await WriteWithSecretsAsync(writer, item, ct);
                writer.WriteEndArray();
                break;

            default:
                // number/bool/null — passthrough.
                el.WriteTo(writer);
                break;
        }
    }
}
