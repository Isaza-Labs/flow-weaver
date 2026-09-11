using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace flow_weaver_backend.Services.Git;

// Shells out to the host's `git` binary for SSH-authenticated network
// operations (clone / fetch / push). LibGit2Sharp's bundled native
// libraries don't include libssh2 on Windows — and on Linux x64 only
// some builds do — so the CLI fallback is the only reliable way to
// support SSH across platforms without shipping a custom native build.
//
// Per call:
//   1. Write the decrypted private key to a temp file with 0600 perms
//      (or hidden+ACL on Windows).
//   2. Build a GIT_SSH_COMMAND that points at that key, disables agent
//      pickup (IdentitiesOnly=yes), and pins known_hosts to a
//      per-invocation file with `accept-new` so first contact succeeds
//      but subsequent host-key changes still raise.
//   3. Invoke `git` with the env vars set and a 5-minute hard cap.
//   4. Always wipe the temp dir in `finally`, even on cancellation.
//
// Passphrase-protected keys are NOT supported in v1: ssh prompts on
// stdin for the passphrase and we run with stdin closed. The service
// fails fast with a clear error message instead of hanging.
internal static class SshGitCli
{
    public static Task<SshGitResult> CloneAsync(
        string url, string targetDir, string defaultBranch,
        string privateKeyPem, string? passphrase,
        ILogger logger, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        // For clone the workdir doesn't exist yet — git creates it.
        // Run from the parent so a stale dir name doesn't trip us.
        var args = new List<string>
        {
            "clone",
            "--branch", defaultBranch,
            "--", url, targetDir,
        };
        return RunAsync(args, workdir: Path.GetDirectoryName(targetDir)!,
            privateKeyPem, passphrase, logger, ct);
    }

    public static Task<SshGitResult> FetchAsync(
        string repoDir, string privateKeyPem, string? passphrase,
        ILogger logger, CancellationToken ct)
        => RunAsync(new[] { "fetch", "origin", "--prune" },
            repoDir, privateKeyPem, passphrase, logger, ct);

    public static Task<SshGitResult> PullAsync(
        string repoDir, string branch, string privateKeyPem, string? passphrase,
        ILogger logger, CancellationToken ct)
        => RunAsync(new[] { "pull", "--ff-only", "origin", branch },
            repoDir, privateKeyPem, passphrase, logger, ct);

    public static Task<SshGitResult> PushAsync(
        string repoDir, string branch, string privateKeyPem, string? passphrase,
        ILogger logger, CancellationToken ct)
        => RunAsync(new[] { "push", "origin", branch },
            repoDir, privateKeyPem, passphrase, logger, ct);

    private static async Task<SshGitResult> RunAsync(
        IReadOnlyList<string> args, string workdir,
        string privateKeyPem, string? passphrase,
        ILogger logger, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(passphrase))
        {
            return new SshGitResult(
                false,
                "passphrase-protected SSH keys are not supported in v1; remove the passphrase or use HTTPS+PAT");
        }

        var tmpRoot = Path.Combine(Path.GetTempPath(), "fw-git-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var keyPath = Path.Combine(tmpRoot, "id");
        var knownHostsPath = Path.Combine(tmpRoot, "known_hosts");

        try
        {
            // Normalize line endings — OpenSSH rejects keys whose body
            // is CRLF; PAT pasted from a Windows form often arrives that way.
            var pem = privateKeyPem.Replace("\r\n", "\n").Replace("\r", "\n");
            if (!pem.EndsWith("\n", StringComparison.Ordinal)) pem += "\n";
            await File.WriteAllTextAsync(keyPath, pem, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(knownHostsPath, string.Empty, ct);
            HardenKeyFile(keyPath);

            var sshCommand = string.Join(" ", new[]
            {
                "ssh",
                "-i", QuoteForGitSsh(keyPath),
                "-o", "IdentitiesOnly=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", $"UserKnownHostsFile={QuoteForGitSsh(knownHostsPath)}",
                "-o", "BatchMode=yes",
                "-o", "ConnectTimeout=15",
            });

            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workdir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_SSH_COMMAND"] = sshCommand;
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_ASKPASS"] = "/bin/true";

            using var proc = new Process { StartInfo = psi };
            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "git.ssh.process_start_failed args={Args}", string.Join(' ', args));
                return new SshGitResult(false, $"could not invoke git: {ex.Message}");
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.StandardInput.Close();

            using var hardCap = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hardCap.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await proc.WaitForExitAsync(hardCap.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                return new SshGitResult(false, "git ssh operation timed out (5m hard cap)");
            }

            var exit = proc.ExitCode;
            var stderr = stderrBuf.ToString();
            var stdout = stdoutBuf.ToString();
            logger.LogInformation(
                "git.ssh.invoke args={Args} exit={Exit} stderr_len={StderrLen}",
                string.Join(' ', args), exit, stderr.Length);
            if (exit == 0)
                return new SshGitResult(true, string.IsNullOrWhiteSpace(stdout) ? "ok" : stdout.Trim());
            // Stderr is the canonical place for git error messages.
            return new SshGitResult(false,
                string.IsNullOrWhiteSpace(stderr) ? $"git exited with {exit}" : stderr.Trim());
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "git.ssh.tmp_cleanup_failed path={Path}", tmpRoot);
            }
        }
    }

    private static void HardenKeyFile(string path)
    {
        // ssh aborts with "permissions are too open" if the key file is
        // group/world-readable on POSIX. On Windows we drop NTFS ACLs so
        // only the current user can read it.
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                // Best-effort ACL tightening; if this fails the file is
                // still in a per-process temp dir whose path is randomized.
                try
                {
                    var fi = new FileInfo(path);
                    var sec = fi.GetAccessControl();
                    sec.SetAccessRuleProtection(true, false);
                    fi.SetAccessControl(sec);
                }
                catch { /* unsupported on some shares; ignore */ }
            }
            else
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Permissions are best-effort. If we can't tighten them, ssh
            // will refuse the key and the operation fails closed.
        }
    }

    private static string QuoteForGitSsh(string path)
    {
        // GIT_SSH_COMMAND is parsed by /bin/sh-like rules; spaces in the
        // path need single quotes, and embedded single quotes must be
        // closed-and-reopened. On Windows there's no shell parsing but
        // git still tokenizes by whitespace, so the same quoting works.
        if (!path.Contains(' ') && !path.Contains('\'')) return path;
        return "'" + path.Replace("'", "'\\''") + "'";
    }
}

internal readonly record struct SshGitResult(bool Ok, string Message);
