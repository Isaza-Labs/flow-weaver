namespace flow_weaver_backend.Services.PythonEnvironments;

// The SSH runner's automatic environment fallback, as a pure decision so it is
// unit-testable without spawning Python.
//
// paramiko 5.0 removed the `ssh-rsa` host-key algorithm. Against a device
// whose sshd offers nothing else (Extreme EXOS / OpenSSH_8.1) the handshake
// fails before authentication with
//     paramiko.ssh_exception.IncompatiblePeer:
//         Incompatible ssh peer (no acceptable host key)
// The SSH handler retries such a failure ONCE with the environment flagged
// IsSshFallback (seeded as `ssh-legacy`, paramiko < 5) — but only when that
// environment is ready and is not the one that just failed.
//
// Retrying is safe: the error is raised during key exchange, before any
// command reached the device.
public static class SshEnvironmentFallback
{
    private static readonly string[] Markers =
    {
        "Incompatible ssh peer",
        "no acceptable host key",
    };

    public static bool IsHostKeyIncompatibility(string? error) =>
        !string.IsNullOrEmpty(error)
        && Markers.Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase));

    // usedEnvironmentId: the environment the failed attempt ran with (null =
    // system python3). fallbackEnvironmentId: the READY fallback environment,
    // or null when there is none.
    public static bool ShouldRetry(string? error, Guid? usedEnvironmentId, Guid? fallbackEnvironmentId) =>
        IsHostKeyIncompatibility(error)
        && fallbackEnvironmentId is not null
        && fallbackEnvironmentId != usedEnvironmentId;
}
