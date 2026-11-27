using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Worker;

namespace flow_weaver_backend.Services.PythonEnvironments;

// Where an environment lives on disk, as a handler needs it.
//   EnvDir      — `<PackagesDir>/envs/<name>`: the stable path. It is a
//                 symlink the builder swaps atomically to the current build.
//   RealEnvDir  — the build directory EnvDir points at right now. The
//                 python_snippet sandbox binds THIS (bwrap needs a real
//                 directory) and runs the interpreter under it, so a swap
//                 mid-run cannot change what the running script sees.
//   BinDir      — `<EnvDir>/bin` (ansible-playbook and friends live here).
//   Interpreter — `<EnvDir>/bin/python`.
public sealed record ResolvedPythonEnvironment(
    Guid Id,
    string Name,
    string EnvDir,
    string RealEnvDir,
    string BinDir,
    string Interpreter)
{
    public string RealInterpreter => Path.Combine(RealEnvDir, "bin", "python");
}

// Thrown when a step asks for an environment it cannot use. The message is
// written for the step's error field: it names the environment and what to do.
public sealed class PythonEnvironmentUnavailableException : Exception
{
    public PythonEnvironmentUnavailableException(string message) : base(message) { }
}

// Maps an environment id to its interpreter. Fails clearly — never falls back
// to the system interpreter silently: a device that was pinned to `ssh-legacy`
// and quietly ran on the image's paramiko would fail with the very error the
// environment exists to avoid, and nobody would see why.
public interface IPythonEnvironmentResolver
{
    // Throws PythonEnvironmentUnavailableException when the environment does
    // not exist, was deleted, is not `ready`, or its directory is missing.
    Task<ResolvedPythonEnvironment> ResolveAsync(Guid environmentId, CancellationToken ct = default);

    // The ready SSH-fallback environment, or null when there is none, it is
    // not ready, it is the one in `excludeId`, or its directory is missing.
    Task<ResolvedPythonEnvironment?> ResolveSshFallbackAsync(Guid? excludeId, CancellationToken ct = default);
}

public sealed class PythonEnvironmentResolver : IPythonEnvironmentResolver
{
    private readonly IPythonEnvironmentRepository _environments;
    private readonly IConfiguration _configuration;

    public PythonEnvironmentResolver(IPythonEnvironmentRepository environments, IConfiguration configuration)
    {
        _environments = environments;
        _configuration = configuration;
    }

    public async Task<ResolvedPythonEnvironment> ResolveAsync(Guid environmentId, CancellationToken ct = default)
    {
        var env = await _environments.GetByIdAsync(environmentId, activeOnly: false, tracking: false, ct);
        if (env is null)
            throw new PythonEnvironmentUnavailableException(
                $"python environment {environmentId} does not exist — pick another environment "
                + "(or System default) on the device / snippet.");
        if (!env.IsActive)
            throw new PythonEnvironmentUnavailableException(
                $"python environment '{env.Name}' was deleted — pick another environment "
                + "(or System default) on the device / snippet.");

        return Locate(env, PythonEnvironmentLayout.EnvsRoot(_configuration));
    }

    public async Task<ResolvedPythonEnvironment?> ResolveSshFallbackAsync(Guid? excludeId, CancellationToken ct = default)
    {
        var env = await _environments.FindActiveSshFallbackAsync(excludeId, ct);
        if (env is null || env.Status != PythonEnvironment.StatusReady) return null;
        try
        {
            return Locate(env, PythonEnvironmentLayout.EnvsRoot(_configuration));
        }
        catch (PythonEnvironmentUnavailableException)
        {
            return null;
        }
    }

    // Exposed for tests: the readiness + on-disk checks without the DB.
    internal static ResolvedPythonEnvironment Locate(PythonEnvironment env, string envsRoot)
    {
        if (env.Status != PythonEnvironment.StatusReady)
            throw new PythonEnvironmentUnavailableException(
                $"python environment '{env.Name}' is not ready (status: {env.Status})"
                + (env.Status == PythonEnvironment.StatusFailed && !string.IsNullOrWhiteSpace(env.LastError)
                    ? $" — last build error: {Truncate(PythonEnvironmentText.MaskCredentials(env.LastError)!, 300)}"
                    : env.Status is PythonEnvironment.StatusPending or PythonEnvironment.StatusBuilding
                        ? " — the worker is building it; retry once it is ready."
                        : string.Empty));

        var envDir = Path.Combine(envsRoot, env.Name);
        var binDir = Path.Combine(envDir, "bin");
        var interpreter = Path.Combine(binDir, "python");

        string realEnvDir;
        try
        {
            realEnvDir = Directory.ResolveLinkTarget(envDir, returnFinalTarget: true)?.FullName ?? envDir;
        }
        catch (Exception)
        {
            realEnvDir = envDir;
        }

        if (!Directory.Exists(envDir) || !File.Exists(interpreter))
            throw new PythonEnvironmentUnavailableException(
                $"python environment '{env.Name}' is marked ready but '{interpreter}' is missing on this "
                + "worker — rebuild it from Govern → Python → Environments (is the python_packages volume "
                + "mounted at the same path on every container?).");

        return new ResolvedPythonEnvironment(env.PythonEnvironmentId, env.Name, envDir, realEnvDir, binDir, interpreter);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

// Where environments live inside the shared python_packages volume. The
// builder writes here and every handler reads from here, so both sides must
// agree on the layout.
public static class PythonEnvironmentLayout
{
    // Sub-directory of Python:PackagesDir holding one directory per environment.
    public const string EnvsDirName = "envs";

    public static string PackagesRoot(IConfiguration configuration)
    {
        var root = (configuration["Python:PackagesDir"] ?? string.Empty).Trim();
        return root.Length == 0 ? PythonPackagesLayout.DefaultPackagesDir : root;
    }

    public static string EnvsRoot(IConfiguration configuration) =>
        Path.Combine(PackagesRoot(configuration), EnvsDirName);
}
