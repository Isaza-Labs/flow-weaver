using System.Diagnostics;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.BackgroundServices;

// Installs the pip packages an admin adds to the python_snippet
// allow-list (AllowedPythonModule, source=pip). Runs OUTSIDE the bwrap sandbox
// — the container has network, only the sandbox is network-isolated — and
// installs into a dedicated dir the sandbox later binds read-only:
//
//   pip3 install --target <PackagesDir>/site [--only-binary :all:] <pip_spec>
//
// Rows are claimed atomically (ClaimNextForProvisionAsync) so multiple worker
// processes never install the same row twice. A successful install + import
// check flips the row to `ready` (the only state the import guard honors); a
// failure records the error for the admin UI.
//
// Security: pip can run packaging code from sdists. We default to wheels-only
// (Python:PipOnlyBinary=true) so nothing arbitrary is built/executed during
// install; admins can relax it per deployment. The snippet itself still runs in
// the no-network bwrap sandbox.
public sealed class PythonPackageProvisionerHostedService : BackgroundService
{
    private const string DefaultPackagesDir = "/app/pyenv";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PythonPackageProvisionerHostedService> _logger;

    public PythonPackageProvisionerHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<PythonPackageProvisionerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    private string PackagesDir =>
        (_config["Python:PackagesDir"] ?? string.Empty).Trim() is { Length: > 0 } d
            ? d
            : DefaultPackagesDir;

    private bool OnlyBinary => _config.GetValue("Python:PipOnlyBinary", true);
    private int IntervalSeconds => Math.Max(5, _config.GetValue("Python:ProvisionIntervalSeconds", 15));
    private int InstallTimeoutSeconds => Math.Max(30, _config.GetValue("Python:PipInstallTimeoutSeconds", 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only Linux containers have pip3 + the package dir; on dev/Windows the
        // python_snippet handler runs bare and dynamic packages don't apply.
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogInformation("PythonPackageProvisioner disabled (non-Linux host)");
            return;
        }

        _logger.LogInformation(
            "PythonPackageProvisioner started — dir={Dir} only_binary={OnlyBinary} interval={Interval}s",
            PackagesDir, OnlyBinary, IntervalSeconds);

        var interval = TimeSpan.FromSeconds(IntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Drain everything claimable this cycle before sleeping.
                while (!stoppingToken.IsCancellationRequested && await ProvisionOneAsync(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PythonPackageProvisioner cycle failed — retrying next interval");
            }

            try { await Task.Delay(interval, stoppingToken); } catch { break; }
        }

        _logger.LogInformation("PythonPackageProvisioner shutting down");
    }

    // Claims and provisions one module. Returns true when it processed a row
    // (so the caller keeps draining), false when there was nothing to do.
    private async Task<bool> ProvisionOneAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAllowedPythonModuleRepository>();

        // Reclaim installs whose claim went stale (a provisioner that died
        // mid-run) after 10 minutes.
        var staleBefore = DateTime.UtcNow.AddMinutes(-10);
        var module = await repo.ClaimNextForProvisionAsync(staleBefore, ct);
        if (module is null) return false;

        var spec = string.IsNullOrWhiteSpace(module.PipSpec) ? module.ImportName : module.PipSpec.Trim();
        var targetDir = Path.Combine(PackagesDir, flow_weaver_backend.Services.Worker.PythonPackagesLayout.SiteDirName);

        _logger.LogInformation(
            "python.pkg.install.begin id={Id} import={Import} spec={Spec} target={Target}",
            module.AllowedPythonModuleId, module.ImportName, spec, targetDir);

        try
        {
            Directory.CreateDirectory(targetDir);

            var (pipExit, pipOut, pipErr) = await InstallAsync(targetDir, spec, ct);
            if (pipExit != 0)
            {
                var err = Tail(string.IsNullOrWhiteSpace(pipErr) ? pipOut : pipErr, 1000);
                await repo.MarkFailedAsync(module.AllowedPythonModuleId, $"pip install failed (exit {pipExit}): {err}", ct);
                _logger.LogWarning(
                    "python.pkg.install.failed id={Id} import={Import} exit={Exit}",
                    module.AllowedPythonModuleId, module.ImportName, pipExit);
                return true;
            }

            // The import name is usually not the package name — python-dateutil imports
            // as dateutil, beautifulsoup4 as bs4, Pillow as PIL — and no rule maps one
            // to the other. Now that it is installed, ask the distribution what it
            // provides rather than making an admin know.
            var discovered = await DiscoverImportNameAsync(targetDir, spec, ct);
            if (discovered is { Length: > 0 } && discovered != module.ImportName)
            {
                if (!await repo.RenameImportNameAsync(module.AllowedPythonModuleId, discovered, ct))
                {
                    await repo.MarkFailedAsync(
                        module.AllowedPythonModuleId,
                        $"'{spec}' imports as '{discovered}', which is already allowed — remove one of the two rows",
                        ct);
                    return true;
                }
                _logger.LogInformation(
                    "python.pkg.import_name id={Id} spec={Spec} import={Import}",
                    module.AllowedPythonModuleId, spec, discovered);
                module.ImportName = discovered;
            }

            // Only a bare identifier is ever interpolated into generated Python. The
            // distribution name a pip row may carry is not one — `python-dateutil`
            // would compile to a syntax error, `ruamel.yaml` to an import of
            // something else — so if discovery could not resolve it, the row fails
            // here with a message rather than reaching the interpolation.
            if (!ImportNameProbeRe.IsMatch(module.ImportName))
            {
                await repo.MarkFailedAsync(
                    module.AllowedPythonModuleId,
                    $"'{spec}' installed, but the module it provides could not be determined — "
                    + "set the import name by hand (the name a script writes after `import`)",
                    ct);
                _logger.LogWarning(
                    "python.pkg.import_name.unresolved id={Id} spec={Spec}",
                    module.AllowedPythonModuleId, spec);
                return true;
            }

            // Verify the module actually imports from the target dir, and grab
            // its version if it exposes __version__.
            var (impExit, version, impErr) = await VerifyImportAsync(targetDir, module.ImportName, ct);
            if (impExit != 0)
            {
                await repo.MarkFailedAsync(
                    module.AllowedPythonModuleId,
                    $"installed but '{module.ImportName}' did not import — check the import name vs the pip package. {Tail(impErr, 500)}",
                    ct);
                _logger.LogWarning(
                    "python.pkg.verify.failed id={Id} import={Import}",
                    module.AllowedPythonModuleId, module.ImportName);
                return true;
            }

            await repo.MarkReadyAsync(module.AllowedPythonModuleId, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), ct);
            _logger.LogInformation(
                "python.pkg.install.ok id={Id} import={Import} version={Version}",
                module.AllowedPythonModuleId, module.ImportName, version.Trim());
            return true;
        }
        catch (Exception ex)
        {
            await repo.MarkFailedAsync(module.AllowedPythonModuleId, Tail(ex.Message, 500), ct);
            _logger.LogError(ex, "python.pkg.install.error id={Id} import={Import}",
                module.AllowedPythonModuleId, module.ImportName);
            return true;
        }
    }

    private async Task<(int Exit, string Stdout, string Stderr)> InstallAsync(
        string targetDir, string spec, CancellationToken ct)
    {
        var args = new List<string>
        {
            "install", "--target", targetDir,
            "--break-system-packages", "--no-cache-dir", "--no-warn-script-location",
            "--disable-pip-version-check",
        };
        // Wheels-only by default: never builds/executes sdist packaging code.
        if (OnlyBinary) { args.Add("--only-binary"); args.Add(":all:"); }
        args.Add(spec); // single token; validated to a safe charset at the API layer.

        return await RunAsync("pip3", args, env: null, InstallTimeoutSeconds, ct);
    }

    /// <summary>
    /// The top-level module an installed distribution provides, or null when it
    /// cannot be determined.
    /// </summary>
    /// <remarks>
    /// Read off the installed distribution — `top_level.txt`, else its record of
    /// installed files — never guessed from the package name. The two are unrelated
    /// often enough to matter and a lookup table would be wrong for the next package.
    ///
    /// Null is not a failure: the caller keeps the name the admin gave and verifies
    /// that instead, which is the behaviour that existed before.
    /// </remarks>
    private async Task<string?> DiscoverImportNameAsync(
        string targetDir, string spec, CancellationToken ct)
    {
        // The distribution name is the spec without extras or a version pin.
        var dist = new string(spec.TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        if (dist.Length == 0) return null;

        const string Probe = """
            import sys
            from importlib import metadata
            dist = sys.argv[1]
            try:
                d = metadata.distribution(dist)
            except Exception:
                print(""); raise SystemExit(0)
            names = []
            top = d.read_text("top_level.txt")
            if top:
                names = [ln.strip() for ln in top.splitlines() if ln.strip()]
            if not names:
                seen = []
                for f in (d.files or []):
                    parts = str(f).split("/")
                    if parts[0].endswith(".dist-info") or parts[0].endswith(".data"):
                        continue
                    if len(parts) > 1 and parts[1] == "__init__.py":
                        seen.append(parts[0])
                    elif len(parts) == 1 and parts[0].endswith(".py"):
                        seen.append(parts[0][:-3])
                names = list(dict.fromkeys(seen))
            if not names:
                print(""); raise SystemExit(0)
            normalised = dist.replace("-", "_").lower()
            for n in names:
                if n.lower() == normalised:
                    print(n); raise SystemExit(0)
            print(names[0])
            """;

        // Same environment VerifyImportAsync uses, so the probe sees exactly what the
        // import will.
        var env = new Dictionary<string, string> { ["PYTHONPATH"] = targetDir + ":/usr/local/lib" };
        var (exit, stdout, _) = await RunAsync(
            "python3", new List<string> { "-c", Probe, dist }, env, timeoutSec: 30, ct);
        if (exit != 0) return null;

        var name = stdout.Trim();
        return ImportNameProbeRe.IsMatch(name) ? name : null;
    }

    private static readonly System.Text.RegularExpressions.Regex ImportNameProbeRe =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<(int Exit, string Stdout, string Stderr)> VerifyImportAsync(
        string targetDir, string importName, CancellationToken ct)
    {
        // Prepend the target dir so the freshly-installed package wins over any
        // same-named system module; keep /usr/local/lib for flowweaver_runtime.
        var env = new Dictionary<string, string> { ["PYTHONPATH"] = targetDir + ":/usr/local/lib" };
        var script = $"import {importName} as _m; print(getattr(_m, '__version__', ''))";
        return await RunAsync("python3", new List<string> { "-c", script }, env, timeoutSec: 30, ct);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env,
        int timeoutSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, string.Empty, $"timed out after {timeoutSec}s");
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Tail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
