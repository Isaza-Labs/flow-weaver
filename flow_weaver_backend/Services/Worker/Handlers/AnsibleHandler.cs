using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Executes an Ansible playbook. The YAML body lives on the Snippet
// row (SnippetRequest.SnippetCode) exactly like python_snippet scripts. We
// write it to a temp file and invoke `ansible-playbook` as a subprocess,
// building the inventory from `request.DeviceId` (or input.hosts for
// ad-hoc runs without a device).
//
// Credentials: when the target device has a CredentialId, we decrypt the
// password and pass it through ANSIBLE_SSH_PASS / ansible_user. Key-based
// auth is not wired yet — matches the SSH handler scope for MVP.
//
// Platform: only Linux containers. Windows returns a clear error rather
// than crashing on a missing `ansible-playbook` binary.
public sealed class AnsibleHandler : ISnippetHandler
{
    public string Type => "ansible_playbook";
    // Playbooks mutate device state with no automatic compensation.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    private readonly IDeviceRepository _devices;
    private readonly IRepository<CredentialModel> _credentials;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ILogger<AnsibleHandler> _logger;

    public AnsibleHandler(
        IDeviceRepository devices,
        IRepository<CredentialModel> credentials,
        ICredentialEncryptionService crypto,
        ILogger<AnsibleHandler> logger)
    {
        _devices = devices;
        _credentials = credentials;
        _crypto = crypto;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Fail("ansible_playbook handler only runs on Linux (container deployment)");

        // `device` / `host` / `targets` are the portable spellings of `hosts`
        // (snippets/SPEC.md `ansible_playbook`).
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);

        // Playbook source: snippet first, then legacy input overrides.
        string? playbook = request.SnippetCode;
        if (string.IsNullOrWhiteSpace(playbook)
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("playbook", out var p))
            playbook = p.GetString();
        if (string.IsNullOrWhiteSpace(playbook))
            return Fail("snippet code is empty — set the playbook on the snippet");

        // Timeout: explicit input override > snippet > 600s default.
        var timeoutSec = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("timeout_seconds", out var t)
            && t.TryGetInt32(out var tv)
                ? tv
                : request.SnippetTimeoutSeconds > 0
                    ? request.SnippetTimeoutSeconds
                    : 600;
        timeoutSec = Math.Clamp(timeoutSec, 10, 3600);

        // Resolve targets: per-step DeviceId preferred, otherwise input.hosts
        // (a single name/address or an array of them). EVERY host runs — a
        // playbook that silently executed against the first of three would
        // report success for two boxes it never touched.
        var (hosts, username, password, targetError) = await ResolveTargetsAsync(request, input, ct);
        if (targetError is not null)
            return Fail(targetError);
        if (hosts.Count == 0)
            return Fail("cannot resolve ansible target — provide input.hosts or a valid device_id");

        // Extra vars: entire InputPayload goes through as JSON on --extra-vars
        // so playbooks can read runtime parameters via `{{ key }}` lookups.
        var extraVarsJson = input.ValueKind == JsonValueKind.Object
            ? input.GetRawText()
            : "{}";

        var runId = Guid.NewGuid().ToString("N");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"fw-ansible-{runId}");
        Directory.CreateDirectory(tmpDir);

        var playbookFile = Path.Combine(tmpDir, "playbook.yml");
        var inventoryFile = Path.Combine(tmpDir, "inventory.ini");

        try
        {
            await File.WriteAllTextAsync(playbookFile, playbook, ct);

            // One line per host. The password is passed via env var rather
            // than on the CLI so it never appears in the process listing —
            // which is also why every host in one step must share a single
            // credential (ResolveTargetsAsync refuses the alternative).
            var inventory = new StringBuilder();
            inventory.AppendLine("[targets]");
            foreach (var h in hosts)
            {
                inventory.Append(h);
                if (!string.IsNullOrWhiteSpace(username))
                    inventory.Append($" ansible_user={username}");
                inventory.AppendLine(" ansible_host_key_checking=False");
            }
            await File.WriteAllTextAsync(inventoryFile, inventory.ToString(), ct);

            var psi = new ProcessStartInfo
            {
                FileName = "ansible-playbook",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tmpDir,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inventoryFile);
            psi.ArgumentList.Add(playbookFile);
            psi.ArgumentList.Add("--extra-vars");
            psi.ArgumentList.Add(extraVarsJson);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("targets");

            // Password + host-key policy travel as env vars only.
            psi.EnvironmentVariables["ANSIBLE_HOST_KEY_CHECKING"] = "False";
            psi.EnvironmentVariables["ANSIBLE_STDOUT_CALLBACK"] = "default";
            if (!string.IsNullOrWhiteSpace(password))
                psi.EnvironmentVariables["ANSIBLE_SSH_PASS"] = password;

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
                return Fail($"ansible-playbook timed out after {timeoutSec}s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var exitCode = proc.ExitCode;
            var success = exitCode == 0;

            // Ansible returns structured state, but without --json it prints
            // human-readable tables. Surface the tail as logs and parse the
            // final "PLAY RECAP" line into a summary for the caller.
            var recaps = hosts
                .Select(h => new { host = h, recap = ParsePlayRecap(stdout, h) })
                .ToList();

            var output = JsonSerializer.SerializeToElement(new
            {
                exit_code = exitCode,
                // `host` / `recap` stay singular for the one-host case every
                // existing template was written against; `hosts` / `recaps`
                // are the honest answer when the step fanned out.
                host = hosts.Count == 1 ? hosts[0] : null,
                hosts,
                recap = hosts.Count == 1 ? recaps[0].recap : null,
                recaps,
            });

            return new SnippetResult
            {
                Success = success,
                Output = output,
                Change = AnyHostChanged(stdout, hosts) ? StepChange.Changed : StepChange.Unchanged,
                Logs = $"exit={exitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}",
                Error = success ? string.Empty : $"exit code {exitCode}: {stderr}".Trim(),
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "ansible-playbook binary missing");
            return Fail("ansible-playbook is not installed in the worker image");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* cleanup best effort */ }
        }
    }

    // Every host this step must run against, with the single credential they
    // share. `hosts` carries INVENTORY DEVICE NAMES as well as literal
    // addresses (snippets/SPEC.md `ansible_playbook`), and `device` / `host` /
    // `targets` are normalized into it before we get here. A value that names a
    // registered device resolves to that device's address and credential;
    // anything else is passed through as the literal address it has always
    // been, so existing playbooks are untouched.
    //
    // Hosts that resolve to DIFFERENT credentials are refused rather than
    // truncated: the runner takes one ANSIBLE_SSH_PASS, and picking one of them
    // would run the playbook against boxes it cannot authenticate to while
    // reporting on the one it could.
    private async Task<(List<string> Hosts, string? Username, string? Password, string? Error)>
        ResolveTargetsAsync(SnippetRequest request, JsonElement input, CancellationToken ct)
    {
        var hosts = new List<string>();
        if (input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("hosts", out var hEl))
        {
            switch (hEl.ValueKind)
            {
                case JsonValueKind.String:
                    var single = hEl.GetString();
                    if (!string.IsNullOrWhiteSpace(single)) hosts.Add(single!.Trim());
                    break;
                case JsonValueKind.Array:
                    foreach (var item in hEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) hosts.Add(v!.Trim());
                    }
                    break;
            }
        }

        if (!request.DeviceId.HasValue && hosts.Count > 0)
        {
            var credentialIds = new HashSet<Guid>();
            for (var i = 0; i < hosts.Count; i++)
            {
                if (System.Net.IPAddress.TryParse(hosts[i], out _)) continue;
                var named = await _devices.FindActiveByNameAsync(hosts[i], ct);
                if (named is null) continue;
                hosts[i] = named.IpAddress;
                if (named.CredentialId != Guid.Empty) credentialIds.Add(named.CredentialId);
            }

            if (credentialIds.Count > 1)
                return (hosts, null, null,
                    "not_supported: the hosts of this step resolve to "
                    + $"{credentialIds.Count} different inventory credentials, and one ansible run "
                    + "carries one credential. Split them into one step per credential — this step "
                    + "will not run against a subset and call it success.");

            if (credentialIds.Count == 1)
            {
                var namedCredential = await _credentials.GetByIdAsync(
                    credentialIds.First(), tracking: false, ct: ct);
                if (namedCredential is not null)
                {
                    try
                    {
                        return (hosts, namedCredential.Username,
                            _crypto.Decrypt(namedCredential.EncryptedPassword), null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "credential {Id} decryption failed", namedCredential.CredentialId);
                    }
                }
            }
        }

        if (request.DeviceId.HasValue)
        {
            var device = await _devices.GetByIdAsync(request.DeviceId.Value, activeOnly: false, tracking: false, ct);
            if (device is not null)
            {
                if (hosts.Count == 0 && !string.IsNullOrWhiteSpace(device.IpAddress))
                    hosts.Add(device.IpAddress);
                if (device.CredentialId != Guid.Empty)
                {
                    var credential = await _credentials.GetByIdAsync(device.CredentialId, tracking: false, ct: ct);
                    if (credential is not null)
                    {
                        try
                        {
                            return (hosts, credential.Username,
                                _crypto.Decrypt(credential.EncryptedPassword), null);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "credential {Id} decryption failed", credential.CredentialId);
                        }
                    }
                }
            }
        }

        return (hosts, null, null, null);
    }

    // Extracts the PLAY RECAP line for the target host so downstream steps
    // can condition on ok/changed/failed counts without parsing full stdout.
    private static object? ParsePlayRecap(string stdout, string host) =>
        ParsePlayRecapLine(stdout, host) is { } line ? new { raw = line } : null;

    private static string? ParsePlayRecapLine(string stdout, string host)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(host, StringComparison.OrdinalIgnoreCase)) continue;
            if (!trimmed.Contains("ok=", StringComparison.Ordinal)) continue;
            return trimmed;
        }
        return null;
    }

    // Ansible reports it: every play recap line carries a `changed=` count, and a
    // playbook that ran and changed nothing is exactly the case the change signal
    // exists to tell apart. Measured, not assumed from the handler's tier.
    //
    // Other engines parse the recap into named counts; this product keeps the raw line,
    // so the same measurement is read off the same text a different way. Structuring
    // the recap here would change the step's OUTPUT, which belongs to the `snippets`
    // family, not to this change.
    private static bool AnyHostChanged(string stdout, IReadOnlyList<string> hosts) =>
        hosts.Any(h => ParsePlayRecapLine(stdout, h) is { } line
            && System.Text.RegularExpressions.Regex.Match(line, "changed=([0-9]+)") is { Success: true } m
            && int.TryParse(m.Groups[1].Value, out var n)
            && n > 0);

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
