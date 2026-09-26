using System.Reflection;
using flow_weaver_backend.Services.Worker.Handlers;

namespace flow_weaver_backend.Tests;

// Verifies the static allow-list layer of the python_snippet sandbox.
// The static check is a private method (CheckDangerousCode) so we reach
// in via reflection — it has no dependencies and is the cheapest layer
// to assert in unit tests. The kernel-sandbox layer (bwrap / nsjail /
// prlimit) is verified manually on the worker image; CI does not
// currently spin up a Linux container for sandbox e2e.
public class PythonHandlerSandboxTests
{
    // Default (non-network) sandbox behavior. CheckDangerousCode is now
    // internal (InternalsVisibleTo), so call it directly instead of reflecting.
    private static string? CheckDangerous(string script) =>
        PythonHandler.CheckDangerousCode(script, networkEnabled: false);

    private static bool IsTransientSandboxStartupFailure(int exitCode, string? stdout, string? stderr)
    {
        var method = typeof(PythonHandler)
            .GetMethod("IsTransientSandboxStartupFailure", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsTransientSandboxStartupFailure not found");
        return (bool)method.Invoke(null, new object?[] { exitCode, stdout, stderr })!;
    }

    private static string SandboxSetupError(string? stderr)
    {
        var method = typeof(PythonHandler)
            .GetMethod("SandboxSetupError", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SandboxSetupError not found");
        return (string)method.Invoke(null, new object?[] { stderr })!;
    }

    private static bool IsSandboxSetupFailure(int exitCode, string? stdout, string? stderr)
    {
        var method = typeof(PythonHandler)
            .GetMethod("IsSandboxSetupFailure", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsSandboxSetupFailure not found");
        return (bool)method.Invoke(null, new object?[] { exitCode, stdout, stderr })!;
    }

    [Theory]
    [InlineData("import os\nos.system('touch /tmp/owned')", "os")]
    [InlineData("import subprocess\nsubprocess.run(['ls'])", "subprocess")]
    [InlineData("import socket\nsocket.socket()", "socket")]
    [InlineData("from os import path", "os")]
    [InlineData("from urllib.request import urlopen", "urllib")]
    [InlineData("import requests\nrequests.get('http://x')", "requests")]
    public void Blocks_disallowed_modules(string script, string blockedModule)
    {
        var error = CheckDangerous(script);
        Assert.NotNull(error);
        Assert.Contains(blockedModule, error);
    }

    [Theory]
    [InlineData("__import__('os').system('x')")]
    [InlineData("eval('1+1')")]
    [InlineData("exec('print(1)')")]
    [InlineData("with open('/etc/passwd') as f: data = f.read()")]
    public void Blocks_dangerous_builtins(string script)
    {
        var error = CheckDangerous(script);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("import json\nimport math\nprint(math.pi)")]
    [InlineData("from datetime import datetime\nprint(datetime.utcnow())")]
    [InlineData("import re; m = re.match('a', 'abc')")]
    [InlineData("import flowweaver_runtime\nflowweaver_runtime.set_output({'x': 1})")]
    [InlineData("import csv, io\nbuf = io.StringIO()")]
    [InlineData("import time\ntime.sleep(0)")]
    public void Allows_safe_modules(string script)
    {
        var error = CheckDangerous(script);
        Assert.Null(error);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Admin allow-list (extraAllowed) — dynamic imports
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Extra_allowed_modules_are_importable()
    {
        var extra = new HashSet<string>(StringComparer.Ordinal) { "django", "yaml" };
        // Not in the static safe set, but the admin allow-list permits them.
        Assert.Null(PythonHandler.CheckDangerousCode("import django", networkEnabled: false, extra));
        Assert.Null(PythonHandler.CheckDangerousCode("from yaml import safe_load", networkEnabled: false, extra));
        // A module in neither set is still blocked.
        var err = PythonHandler.CheckDangerousCode("import requests", networkEnabled: false, extra);
        Assert.NotNull(err);
        Assert.Contains("requests", err);
    }

    [Fact]
    public void Extra_allowed_does_not_lift_builtin_bans()
    {
        var extra = new HashSet<string>(StringComparer.Ordinal) { "django" };
        // exec/eval/open/__import__ stay blocked regardless of the allow-list.
        Assert.NotNull(PythonHandler.CheckDangerousCode("import django\neval('1+1')", networkEnabled: false, extra));
        Assert.NotNull(PythonHandler.CheckDangerousCode("with open('/etc/passwd') as f: pass", networkEnabled: false, extra));
    }

    [Fact]
    public void Multiline_imports_each_validated()
    {
        // Both names on the comma-separated import must be checked.
        var error = CheckDangerous("import json, os");
        Assert.NotNull(error);
        Assert.Contains("os", error);
    }

    [Fact]
    public void Aliased_import_is_checked_against_top_module()
    {
        var error = CheckDangerous("import socket as s");
        Assert.NotNull(error);
        Assert.Contains("socket", error);
    }

    [Fact]
    public void Dotted_import_only_top_module_is_checked()
    {
        // `os.path` → top is `os`, blocked.
        var error = CheckDangerous("import os.path");
        Assert.NotNull(error);
        Assert.Contains("os", error);
    }

    [Fact]
    public void From_dotted_import_only_top_module_is_checked()
    {
        var error = CheckDangerous("from os.path import join");
        Assert.NotNull(error);
        Assert.Contains("os", error);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Transient sandbox-setup failure detection (retry gate)
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    // The canonical bwrap message — the one users keep hitting under fan-out.
    [InlineData("bwrap: No permissions to create new namespace, likely because the kernel does not allow non-privileged user namespaces.")]
    [InlineData("bwrap: Creating new namespace failed: Operation not permitted")]
    [InlineData("nsjail: clone() failed: max_user_namespaces limit reached")]
    public void Transient_when_setup_failed_with_empty_stdout(string stderr)
    {
        Assert.True(IsTransientSandboxStartupFailure(1, "", stderr));
        // Whitespace-only stdout is still "the script produced nothing".
        Assert.True(IsTransientSandboxStartupFailure(1, "  \n", stderr));
    }

    // Moved OUT of the retry set above. "setting up uid map: Permission denied"
    // is not namespace contention: the namespace was created and writing
    // /proc/self/uid_map was refused, which on Ubuntu 23.10+ is
    // `kernel.apparmor_restrict_unprivileged_userns=1` — a fixed host policy
    // that four retries with backoff cannot change. It is still a setup failure,
    // so it keeps its actionable message (SandboxUidMapDiagnosticTests).
    [Theory]
    [InlineData("bwrap: setting up uid map: Permission denied")]
    [InlineData("bwrap: setting up gid map: Permission denied")]
    public void A_uid_map_denial_is_a_setup_failure_but_not_retried(string stderr)
    {
        Assert.False(IsTransientSandboxStartupFailure(1, "", stderr));
        Assert.True(IsSandboxSetupFailure(1, "", stderr));
    }

    [Fact]
    public void Not_transient_on_success()
    {
        Assert.False(IsTransientSandboxStartupFailure(0, "{\"ok\":true}", ""));
    }

    [Fact]
    public void Not_transient_when_stdout_present()
    {
        // Defense in depth: a script that produced output must never be
        // re-run, even if its stderr happens to mention a namespace.
        Assert.False(IsTransientSandboxStartupFailure(
            1, "{\"partial\":1}", "bwrap: No permissions to create new namespace"));
    }

    [Fact]
    public void Not_transient_on_real_script_error()
    {
        Assert.False(IsTransientSandboxStartupFailure(
            1, "", "Traceback (most recent call last):\n  KeyError: 'host'"));
    }

    [Fact]
    public void Not_transient_when_no_stderr()
    {
        Assert.False(IsTransientSandboxStartupFailure(1, "", ""));
    }

    // ─────────────────────────────────────────────────────────────────
    //  Exhausted-retry error message routes by errno
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Setup_error_eagain_points_to_nproc()
    {
        var msg = SandboxSetupError(
            "bwrap: Creating new namespace failed: Resource temporarily unavailable");
        Assert.Contains("Python:MaxProcesses", msg);
        Assert.Contains("EAGAIN", msg);
        Assert.DoesNotContain("seccomp", msg);
    }

    [Fact]
    public void Setup_error_eperm_points_to_seccomp()
    {
        var msg = SandboxSetupError(
            "bwrap: No permissions to create new namespace, likely because the kernel does not allow non-privileged user namespaces.");
        Assert.Contains("seccomp", msg);
        Assert.DoesNotContain("Python:MaxProcesses", msg);
    }

    [Fact]
    public void Setup_error_proc_mount_points_to_systempaths()
    {
        var msg = SandboxSetupError(
            "bwrap: Can't mount proc on /newroot/proc: Operation not permitted");
        Assert.Contains("systempaths=unconfined", msg);
        // Must not be mis-routed to the seccomp branch (it also says EPERM).
        Assert.DoesNotContain("the default seccomp profile", msg);
    }

    [Fact]
    public void Proc_mount_failure_is_a_setup_failure_but_not_retried()
    {
        // Surfaced with actionable guidance (setup failure)…
        Assert.True(IsSandboxSetupFailure(1, "", "bwrap: Can't mount proc on /newroot/proc: Operation not permitted"));
        // …but NOT retried (deterministic — masking won't change between tries).
        Assert.False(IsTransientSandboxStartupFailure(1, "", "bwrap: Can't mount proc on /newroot/proc: Operation not permitted"));
    }

    // ─────────────────────────────────────────────────────────────────
    //  bwrap mount ordering
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bwrap_mounts_tmpfs_before_binding_the_script()
    {
        var m = typeof(PythonHandler)
            .GetMethod("BuildBwrap", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildBwrap not found");
        var (_, args) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", false, null })!;

        var tmpfsIdx = args.IndexOf("/tmp");                 // value after "--tmpfs"
        var scriptBindIdx = args.IndexOf("/tmp/fw-test.py"); // first occurrence = --ro-bind source

        Assert.True(tmpfsIdx >= 0, "expected a --tmpfs /tmp mount");
        Assert.True(scriptBindIdx >= 0, "expected the script to be bound into the sandbox");
        // The tmpfs MUST precede the script bind, or it shadows the script
        // and python3 fails with "can't open file … No such file or directory".
        Assert.True(tmpfsIdx < scriptBindIdx,
            $"--tmpfs /tmp (idx {tmpfsIdx}) must come before the script bind (idx {scriptBindIdx})");
    }

    [Fact]
    public void Bwrap_default_has_no_network_but_network_enabled_shares_net()
    {
        var m = typeof(PythonHandler)
            .GetMethod("BuildBwrap", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildBwrap not found");

        var (_, isolated) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", false, null })!;
        Assert.DoesNotContain("--share-net", isolated);   // default = no network namespace
        Assert.DoesNotContain("/etc/resolv.conf", isolated);

        var (_, networked) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", true, null })!;
        Assert.Contains("--share-net", networked);        // network-enabled = host net + DNS
        Assert.Contains("/etc/resolv.conf", networked);
    }

    [Fact]
    public void Integrations_in_an_isolated_bwrap_sandbox_fail_fast_with_the_fix()
    {
        // Reproduced in the worker image: integration() from a snippet without
        // network_enabled died with "Temporary failure in name resolution".
        var error = PythonHandler.IntegrationsWithoutNetworkError("bwrap", false, new[] { "netbox" });

        Assert.NotNull(error);
        Assert.Contains("'netbox'", error);
        Assert.Contains("Network enabled", error);
        Assert.Contains("integration_action", error);
    }

    [Theory]
    [InlineData("bwrap", true)]    // network-enabled: the sandbox shares the host net
    [InlineData("none", false)]    // Development: bare python3, host network
    [InlineData("nsjail", false)]  // operator-configured, may grant network
    [InlineData("custom", false)]
    public void Integrations_are_left_alone_where_the_sandbox_may_have_network(string mode, bool networkEnabled)
    {
        Assert.Null(PythonHandler.IntegrationsWithoutNetworkError(mode, networkEnabled, new[] { "netbox" }));
    }

    [Fact]
    public void A_snippet_without_integrations_is_not_affected()
    {
        Assert.Null(PythonHandler.IntegrationsWithoutNetworkError("bwrap", false, Array.Empty<string>()));
    }

    [Fact]
    public void Bwrap_network_enabled_binds_the_public_ca_store_only()
    {
        // Debian's OpenSSL default paths are symlinks into /etc/ssl. Without
        // the certs bound, HTTPS from a network-enabled snippet failed with
        // CERTIFICATE_VERIFY_FAILED (reproduced in the worker image).
        var m = typeof(PythonHandler)
            .GetMethod("BuildBwrap", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildBwrap not found");

        var (_, networked) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", true, null })!;
        Assert.Contains("/etc/ssl/certs", networked);
        Assert.DoesNotContain(networked, a => a.StartsWith("/etc/ssl/private", StringComparison.Ordinal));
        Assert.DoesNotContain("/etc/ssl", networked);   // the whole dir would include private/

        var (_, isolated) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", false, null })!;
        Assert.DoesNotContain("/etc/ssl/certs", isolated);
    }

    [Fact]
    public void Bwrap_binds_package_dir_and_sets_pythonpath_when_provided()
    {
        var m = typeof(PythonHandler)
            .GetMethod("BuildBwrap", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildBwrap not found");

        const string pkg = "/app/pyenv/abc123";
        var (_, args) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", false, pkg })!;
        // The package dir is bound read-only and prepended to PYTHONPATH.
        Assert.Contains(pkg, args);
        var ppIdx = args.IndexOf("PYTHONPATH");
        Assert.True(ppIdx >= 0 && args[ppIdx + 1].StartsWith(pkg + ":", StringComparison.Ordinal),
            "expected --setenv PYTHONPATH <pkgDir>:/usr/local/lib");

        // Without a package dir, no PYTHONPATH override is added.
        var (_, none) = ((string, List<string>))m.Invoke(null, new object?[] { "/tmp/fw-test.py", false, null })!;
        Assert.DoesNotContain("PYTHONPATH", none);
    }
}
