using System.Diagnostics;
using System.Runtime.InteropServices;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.PythonEnvironments;

namespace flow_weaver_backend.BackgroundServices;

// Builds admin-defined Python environments (PythonEnvironment) with uv, at
// RUNTIME — no image rebuild. Modelled on PythonPackageProvisionerHostedService
// and registered exactly where it is (worker-only, Linux-only): the worker is
// the single writer to the shared python_packages volume (/app/pyenv), which
// the backend container mounts too, so both see every build.
//
// One build, for a claimed row:
//
//   uv venv <envs>/<name>.<id>.tmp --python <ver|python3> --relocatable
//   uv pip install --python <tmp>/bin/python -r <req> [--only-binary :all:]
//   uv pip freeze  --python <tmp>/bin/python
//
// then the build is renamed to <envs>/<name>.<id> and the stable path
// <envs>/<name> — a SYMLINK — is atomically re-pointed at it with rename(2).
// A running job that already resolved the old target keeps using it (the
// previous build is kept on disk until the next swap), and a job starting now
// sees either the old build or the new one, never a half-built env.
//
// `--relocatable` makes the venv's console scripts (`ansible-playbook`, …)
// find their interpreter relative to themselves, so they keep working after
// the rename from the .tmp directory.
//
// What gets installed is the environment's EFFECTIVE requirements: its own
// Requirements plus the pip specs of the Packages-allowlist rows that target
// it (python_module_environments), merged and de-duplicated by
// PythonEnvironmentText.BuildEffectiveRequirements. The hash and the stale-
// claim check use the effective text too, so a target change made during a
// build is never marked ready with the old package set.
//
// Rows are claimed atomically (ClaimNextForBuildAsync) so multiple worker
// replicas never build the same env twice. Success records `uv pip freeze`;
// failure records the (masked, truncated) error and leaves the previous good
// build in place.
//
// Security: installing packages runs third-party code (sdist builds) — which
// is why only admins can define environments. Wheels-only is honoured from
// the same setting the pip provisioner uses (Python:PipOnlyBinary, default
// true). The requirements file (which may carry index credentials) is written
// to a private temp dir, mode 0600, and deleted after the build.
public sealed class PythonEnvironmentBuilderHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PythonEnvironmentBuilderHostedService> _logger;

    public PythonEnvironmentBuilderHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<PythonEnvironmentBuilderHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    private string PackagesRoot => PythonEnvironmentLayout.PackagesRoot(_config);
    private string EnvsRoot => PythonEnvironmentLayout.EnvsRoot(_config);
    private bool OnlyBinary => _config.GetValue("Python:PipOnlyBinary", true);
    private int IntervalSeconds => Math.Max(5, _config.GetValue("Python:ProvisionIntervalSeconds", 15));
    private int StepTimeoutSeconds => Math.Max(60, _config.GetValue("Python:EnvBuildTimeoutSeconds", 900));
    private string UvExecutable =>
        (_config["Python:UvExecutable"] ?? string.Empty).Trim() is { Length: > 0 } uv ? uv : "uv";
    private string DefaultInterpreter =>
        (_config["Python:Executable"] ?? string.Empty).Trim() is { Length: > 0 } py ? py : "python3";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same gate as the pip provisioner: only Linux containers have uv and
        // the shared volume.
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogInformation("PythonEnvironmentBuilder disabled (non-Linux host)");
            return;
        }

        _logger.LogInformation(
            "PythonEnvironmentBuilder started — envs={Dir} uv={Uv} only_binary={OnlyBinary} interval={Interval}s",
            EnvsRoot, UvExecutable, OnlyBinary, IntervalSeconds);

        var interval = TimeSpan.FromSeconds(IntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && await BuildOneAsync(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PythonEnvironmentBuilder cycle failed — retrying next interval");
            }

            try { await Task.Delay(interval, stoppingToken); } catch { break; }
        }

        _logger.LogInformation("PythonEnvironmentBuilder shutting down");
    }

    // Claims and builds one environment. True when a row was processed.
    private async Task<bool> BuildOneAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPythonEnvironmentRepository>();

        // A build runs three uv steps, each capped at StepTimeoutSeconds; a
        // claim older than that (plus slack) belongs to a builder that died.
        var staleBefore = DateTime.UtcNow.AddSeconds(-(StepTimeoutSeconds * 3 + 300));
        var env = await repo.ClaimNextForBuildAsync(staleBefore, ct);
        if (env is null) return false;

        var claimedRequirements = env.Requirements;
        var claimedVersion = env.PythonVersion;
        // Own requirements + the specs the Packages allowlist targets at this
        // environment. This is what is installed and hashed.
        var effectiveRequirements = await repo.GetEffectiveRequirementsAsync(env, ct);
        var claim = PythonEnvironmentBuildGuard.Capture(env, effectiveRequirements);
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var buildDir = Path.Combine(EnvsRoot, $"{env.Name}.{shortId}");
        var tmpDir = buildDir + ".tmp";
        var privateDir = Path.Combine(Path.GetTempPath(), $"fw-pyenv-{shortId}");
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "python.env.build.begin id={Id} name={Name} python={Python} dir={Dir}",
            env.PythonEnvironmentId, env.Name, claimedVersion ?? "(image python3)", tmpDir);

        try
        {
            Directory.CreateDirectory(EnvsRoot);
            Directory.CreateDirectory(privateDir);
            TrySetMode(privateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Requirements may hold index credentials: private dir, 0600,
            // deleted in `finally`. Never inside the env (which is bound into
            // python_snippet sandboxes).
            var reqFile = Path.Combine(privateDir, "requirements.txt");
            await File.WriteAllTextAsync(reqFile, effectiveRequirements, ct);
            TrySetMode(reqFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // 1. venv
            var venvArgs = new List<string>
            {
                "venv", tmpDir,
                "--python", string.IsNullOrWhiteSpace(claimedVersion) ? DefaultInterpreter : claimedVersion!,
                "--relocatable", "--no-config",
            };
            var (exit, stdout, stderr) = await RunUvAsync(venvArgs, privateDir, ct);
            if (exit != 0)
                return await FailAsync(repo, env, claimedRequirements, claimedVersion, tmpDir,
                    $"uv venv failed (exit {exit}): {Tail(Pick(stderr, stdout), 1500)}", ct);

            var python = Path.Combine(tmpDir, "bin", "python");

            // 2. install
            var installArgs = new List<string>
            {
                "pip", "install", "--python", python, "--no-config", "-r", reqFile,
            };
            // Wheels-only unless the deployment relaxed it — never builds or
            // executes sdist packaging code during install.
            if (OnlyBinary) { installArgs.Add("--only-binary"); installArgs.Add(":all:"); }
            (exit, stdout, stderr) = await RunUvAsync(installArgs, privateDir, ct);
            if (exit != 0)
                return await FailAsync(repo, env, claimedRequirements, claimedVersion, tmpDir,
                    $"uv pip install failed (exit {exit}): {Tail(Pick(stderr, stdout), 1500)}", ct);

            // 3. freeze — the record of what actually resolved.
            (exit, stdout, stderr) = await RunUvAsync(
                new List<string> { "pip", "freeze", "--python", python, "--no-config" }, privateDir, ct);
            if (exit != 0)
                return await FailAsync(repo, env, claimedRequirements, claimedVersion, tmpDir,
                    $"uv pip freeze failed (exit {exit}): {Tail(Pick(stderr, stdout), 1500)}", ct);
            var freeze = stdout.Trim();

            // The definition may have been edited while we built: the row's
            // own requirements / version / name, or the allowlist rows that
            // target it. Either way the row is already back to `pending`:
            // drop this build rather than swap in packages nobody asked for
            // any more. Compared on the EFFECTIVE requirements.
            var current = await repo.GetByIdAsync(env.PythonEnvironmentId, activeOnly: false, tracking: false, ct);
            var currentEffective = current is null ? null : await repo.GetEffectiveRequirementsAsync(current, ct);
            if (!PythonEnvironmentBuildGuard.IsStillCurrent(claim, current, currentEffective))
            {
                TryDeleteDir(tmpDir);
                _logger.LogInformation(
                    "python.env.build.superseded id={Id} name={Name} — definition changed or row deleted during build",
                    env.PythonEnvironmentId, env.Name);
                return true;
            }

            // 4. atomic swap.
            Directory.Move(tmpDir, buildDir);
            var previous = SwapSymlink(env.Name, buildDir);

            var hash = PythonEnvironmentText.ComputeHash(claimedVersion, effectiveRequirements);
            var marked = await repo.MarkReadyAsync(
                env.PythonEnvironmentId, claimedRequirements, claimedVersion, freeze, hash, ct);

            CleanupOldBuilds(env.Name, keep: new[] { buildDir, previous });

            sw.Stop();
            _logger.LogInformation(
                "python.env.build.ok id={Id} name={Name} duration_ms={DurationMs} packages={Count} marked={Marked}",
                env.PythonEnvironmentId, env.Name, sw.ElapsedMilliseconds,
                freeze.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, marked);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-build: leave the row `building`; the stale
            // reclaim picks it up after restart. Remove the half-built dir.
            TryDeleteDir(tmpDir);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "python.env.build.error id={Id} name={Name}", env.PythonEnvironmentId, env.Name);
            return await FailAsync(repo, env, claimedRequirements, claimedVersion, tmpDir, Tail(ex.Message, 1000), ct);
        }
        finally
        {
            TryDeleteDir(privateDir);
        }
    }

    private async Task<bool> FailAsync(
        IPythonEnvironmentRepository repo, PythonEnvironment env,
        string claimedRequirements, string? claimedVersion, string tmpDir, string error, CancellationToken ct)
    {
        TryDeleteDir(tmpDir);
        var masked = PythonEnvironmentText.MaskCredentials(error) ?? string.Empty;
        await repo.MarkFailedAsync(env.PythonEnvironmentId, claimedRequirements, claimedVersion, masked, ct);
        _logger.LogWarning(
            "python.env.build.failed id={Id} name={Name} error={Error}",
            env.PythonEnvironmentId, env.Name, Tail(masked, 300));
        return true;
    }

    // Points <envs>/<name> at `buildDir` atomically. Returns the build the
    // link pointed at before (kept on disk for jobs still using it), or null.
    private string? SwapSymlink(string name, string buildDir)
    {
        var linkPath = Path.Combine(EnvsRoot, name);
        string? previous = null;

        var info = new DirectoryInfo(linkPath);
        if (info.Exists || info.LinkTarget is not null)
        {
            if (info.LinkTarget is not null)
            {
                previous = Path.GetFullPath(info.LinkTarget, EnvsRoot);
            }
            else
            {
                // A real directory at the stable path (created by hand, or by a
                // pre-symlink layout): move it aside once. This single swap is
                // not atomic; every later one is.
                var aside = Path.Combine(EnvsRoot, $"{name}.{Guid.NewGuid().ToString("N")[..8]}");
                Directory.Move(linkPath, aside);
                previous = aside;
            }
        }

        // Relative target, so the link survives the volume being mounted at a
        // different path in another container.
        var tmpLink = Path.Combine(EnvsRoot, $".{name}.link-{Guid.NewGuid().ToString("N")[..8]}");
        File.CreateSymbolicLink(tmpLink, Path.GetFileName(buildDir));
        if (Rename(tmpLink, linkPath) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            try { File.Delete(tmpLink); } catch { /* best effort */ }
            throw new IOException($"rename({tmpLink} → {linkPath}) failed: errno {errno}");
        }
        return previous;
    }

    // Removes builds of `name` other than the ones to keep (the new one and
    // the one it replaced), plus leftover .tmp dirs from crashed builds.
    private void CleanupOldBuilds(string name, IEnumerable<string?> keep)
    {
        var keepSet = keep.Where(k => k is not null)
            .Select(k => Path.GetFullPath(k!))
            .ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(EnvsRoot, name + ".*"))
            {
                // Directory.EnumerateDirectories follows the symlink entry too;
                // the stable link is `name` exactly, which `name.*` excludes.
                if (keepSet.Contains(Path.GetFullPath(dir))) continue;
                if (new DirectoryInfo(dir).LinkTarget is not null) continue;
                TryDeleteDir(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "python.env.cleanup.failed name={Name}", name);
        }
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunUvAsync(
        IReadOnlyList<string> args, string workingDir, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["UV_NO_PROGRESS"] = "1",
            // Keep the package cache and any uv-managed interpreters on the
            // shared volume: rebuilds after a restart are fast, and a venv
            // built on a downloaded CPython is usable from every container.
            ["UV_CACHE_DIR"] = Environment.GetEnvironmentVariable("UV_CACHE_DIR")
                is { Length: > 0 } cache ? cache : Path.Combine(PackagesRoot, "uv-cache"),
            ["UV_PYTHON_INSTALL_DIR"] = Environment.GetEnvironmentVariable("UV_PYTHON_INSTALL_DIR")
                is { Length: > 0 } pyDir ? pyDir : Path.Combine(PackagesRoot, "python"),
        };

        var psi = new ProcessStartInfo
        {
            FileName = UvExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        // The worker's PYTHONPATH (/usr/local/lib, the runner's helpers) must
        // not leak into what uv inspects or installs.
        psi.Environment.Remove("PYTHONPATH");

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, $"could not start '{UvExecutable}': {ex.Message}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(StepTimeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, string.Empty, $"timed out after {StepTimeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string oldPath, string newPath);

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, mode); } catch { /* best effort */ }
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static string Pick(string stderr, string stdout) =>
        string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

    private static string Tail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
