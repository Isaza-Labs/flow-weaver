using System.Diagnostics;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Validation;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Runs one or more commands on a remote device over SSH by spawning the
// Python-based Netmiko runner shipped at /usr/local/lib/flow_weaver_ssh_runner.py.
//
// Why Python / Netmiko instead of Renci.SshNet (which we used before):
//   - Netmiko wraps Paramiko with vendor-aware CLI handling: prompt
//     detection, paging disablement, enable-mode, TextFSM-structured
//     output. Renci talked SSH correctly but left all of that to us.
//   - Adding Cisco/Juniper/Arista/Nokia quirks in C# would have been
//     duplicate effort; Netmiko already ships 40+ device_types.
//   - The runner is our own code (not an end-user python_snippet) so it
//     runs outside PythonHandler's import allowlist.
//
// Responsibilities that stay in C# (NOT in the runner):
//   - Parsing input payload (`command` / `commands`, `host`, `port`,
//     `stop_on_error`, `timeout_seconds`, `credential_id`, `enable_secret`,
//     `use_structured`, `device_type`).
//   - Device + Credential lookup + decryption.
//   - Policy gate per-command (`PolicyEvaluator.ssh_command_regex`) —
//     runs BEFORE each command is sent to the runner so a deny never
//     reaches the network.
//   - Merging policy denials with runner results into the final output.
//
// Responsibilities delegated to the runner:
//   - TCP connect, SSH handshake, auth (password or key).
//   - Host-key pinning against Device.ExpectedSshHostKeyFingerprint.
//   - Prompt detection, paging, enable-mode entry.
//   - Per-command execution and output capture.
//
// Output shape (back-compat with Phase 1/2 templates):
//   {
//     exit_code: int,                       // synthesized: 0 if last cmd ok
//     stdout: string,                       // last cmd's output (ANSI stripped)
//     stdout_raw: string,                   // last cmd's output (untouched)
//     stderr: string,                       // last cmd's error (if any)
//     host_key_fingerprint: "SHA256:…",     // surfaced by runner
//     device_type: "cisco_ios",             // what was used
//     results: [
//       { command, output, output_raw, parsed?, elapsed_ms, ok, error?,
//         blocked_by_policy?, policy_name? }
//     ]
//   }
//
// `output` and `stdout` have ANSI/control sequences stripped by the runner.
// `output_raw` / `stdout_raw` keep the bytes Netmiko returned. Set
// `preserve_ansi: true` in the node's config_overrides to leave both
// fields untouched (rare; only useful when the consumer parses ANSI).
public sealed class SshHandler : ISnippetHandler
{
    public string Type => "ssh";
    // `show ...` and other read-only CLI commands are idempotent, but
    // any config write requires manual rollback per device. Default
    // to NonReversible so a workflow that pushes config to a router
    // surfaces a promotion warning instead of being silently approved.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    private const string DefaultRunnerPath = "/usr/local/lib/flow_weaver_ssh_runner.py";
    private const string DefaultPythonExecutable = "python3";

    private readonly IDeviceRepository _devices;
    private readonly IRepository<CredentialModel> _credentials;
    private readonly IWorkflowRunRepository _runs;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IPolicyEvaluator _policies;
    private readonly IVendorCommandValidator _vendorCommands;
    private readonly ISecretResolver _secrets;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SshHandler> _logger;

    public SshHandler(
        IDeviceRepository devices,
        IRepository<CredentialModel> credentials,
        IWorkflowRunRepository runs,
        ICredentialEncryptionService crypto,
        IPolicyEvaluator policies,
        IVendorCommandValidator vendorCommands,
        ISecretResolver secrets,
        IConfiguration configuration,
        ILogger<SshHandler> logger)
    {
        _devices = devices;
        _credentials = credentials;
        _runs = runs;
        _crypto = crypto;
        _policies = policies;
        _vendorCommands = vendorCommands;
        _secrets = secrets;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        // `structured` / `enable` are the portable spellings of
        // `use_structured` / `enable_secret` (snippets/SPEC.md `ssh`).
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);

        var commands = ResolveCommands(input, out var commandsError);
        if (commandsError is not null)
            return Fail(commandsError);

        var stopOnError = !input.TryGetProperty("stop_on_error", out var soeEl)
            || soeEl.ValueKind != JsonValueKind.False;

        string? host = input.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
        var port = input.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out var pv) ? pv : 22;
        var timeoutSec = input.TryGetProperty("timeout_seconds", out var tEl) && tEl.TryGetInt32(out var tv) ? tv : 30;
        timeoutSec = Math.Clamp(timeoutSec, 5, 300);

        Guid? credentialId = null;
        if (input.TryGetProperty("credential_id", out var cidEl)
            && cidEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(cidEl.GetString(), out var parsedCid))
        {
            credentialId = parsedCid;
        }

        // Inline ("custom") credentials passed directly in the step input —
        // a one-off override that does NOT require a stored Credential row.
        // When `username` is present these take precedence over
        // input.credential_id and the device's own credential. They arrive
        // as plaintext over the authenticated API (no decryption) and are
        // sent to the runner over stdin; the handler never logs them.
        // Caveat: unlike a stored Credential (encrypted at rest), inline
        // secrets persist in the step input payload — prefer a stored
        // Credential for committed/long-lived workflows.
        string? inlineUsername = input.TryGetProperty("username", out var unEl)
            && unEl.ValueKind == JsonValueKind.String ? unEl.GetString() : null;
        string? inlinePassword = input.TryGetProperty("password", out var pwEl)
            && pwEl.ValueKind == JsonValueKind.String ? pwEl.GetString() : null;
        string? inlinePrivateKey = input.TryGetProperty("private_key", out var pkEl)
            && pkEl.ValueKind == JsonValueKind.String ? pkEl.GetString() : null;
        string? inlineKeyPassphrase = input.TryGetProperty("key_passphrase", out var kpEl)
            && kpEl.ValueKind == JsonValueKind.String ? kpEl.GetString() : null;

        // The importer ACCEPTS an inline credential only when every value is a
        // `${secret:...}` reference, and lists it in requires.secrets — on the
        // promise that "a handler that receives a ${secret:...} value resolves
        // it at run time" (snippets/SPEC.md `ssh`). Without this the marker was
        // sent to the runner as the literal password, so the reference the
        // importer had deliberately preserved failed authentication and landed
        // in the connection log.
        inlineUsername = await ResolveSecretsAsync(inlineUsername, ct);
        inlinePassword = await ResolveSecretsAsync(inlinePassword, ct);
        inlinePrivateKey = await ResolveSecretsAsync(inlinePrivateKey, ct);
        inlineKeyPassphrase = await ResolveSecretsAsync(inlineKeyPassphrase, ct);

        string? explicitDeviceType = input.TryGetProperty("device_type", out var dtEl)
            && dtEl.ValueKind == JsonValueKind.String
                ? dtEl.GetString()
                : null;
        string? enableSecret = input.TryGetProperty("enable_secret", out var esEl)
            && esEl.ValueKind == JsonValueKind.String
                ? esEl.GetString()
                : null;
        enableSecret = await ResolveSecretsAsync(enableSecret, ct);
        var useStructured = input.TryGetProperty("use_structured", out var usEl)
            && usEl.ValueKind == JsonValueKind.True;
        var preserveAnsi = input.TryGetProperty("preserve_ansi", out var paEl)
            && paEl.ValueKind == JsonValueKind.True;
        // direct_exec switches the runner from Netmiko's interactive
        // shell to paramiko's exec_command (no PTY) — required for Nokia
        // SR Linux whose interactive CLI emits cursor-redraw escapes that
        // confuse Netmiko's prompt-regex reader. Tri-state: when absent
        // the runner picks a per-vendor default (auto-true for
        // device_type=nokia_srl, false otherwise).
        bool? directExec = null;
        if (input.TryGetProperty("direct_exec", out var deEl))
        {
            if (deEl.ValueKind == JsonValueKind.True) directExec = true;
            else if (deEl.ValueKind == JsonValueKind.False) directExec = false;
        }
        var useTiming = input.TryGetProperty("use_timing", out var utEl)
            && utEl.ValueKind == JsonValueKind.True;
        string? readUntilPattern = input.TryGetProperty("read_until_pattern", out var rupEl)
            && rupEl.ValueKind == JsonValueKind.String
                ? rupEl.GetString()
                : null;
        var setupCommands = new List<string>();
        if (input.TryGetProperty("setup_commands", out var scEl) && scEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in scEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) setupCommands.Add(s!);
            }
        }

        string? expectedFingerprint = null;
        string? deviceType = explicitDeviceType;

        if (request.DeviceId.HasValue)
        {
            var device = await _devices.FindByIdUnscopedAsync(request.DeviceId.Value, ct);
            if (device is not null)
            {
                host ??= device.IpAddress;
                // ??= on Guid? only replaces null — a device row with
                // CredentialId = Guid.Empty means "no credential" so we
                // explicitly short-circuit that to null below.
                if (credentialId is null)
                    credentialId = device.CredentialId == Guid.Empty ? null : device.CredentialId;
                expectedFingerprint = device.ExpectedSshHostKeyFingerprint;
                if (string.IsNullOrWhiteSpace(deviceType) && !string.IsNullOrWhiteSpace(device.Platform))
                    deviceType = device.Platform;
            }
        }

        // `device` names an inventory device — by name or by id — and is the
        // CANONICAL way a portable ssh step picks its target
        // (snippets/SPEC.md `ssh`). In per_device mode request.DeviceId
        // already holds the current device and wins; this is what lets a
        // `once` step name one. A name that matches nothing is `not_found`,
        // never an ad-hoc connection to whatever that string happens to
        // resolve to on the network. A node carrying both `device` and the
        // `host` alias means `device` (bundle/SPEC.md §4), so the address read
        // above is discarded here rather than kept.
        if (!request.DeviceId.HasValue
            && input.TryGetProperty("device", out var deviceEl)
            && deviceEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(deviceEl.GetString()))
        {
            var wanted = deviceEl.GetString()!.Trim();
            var named = Guid.TryParse(wanted, out var wantedId)
                ? await _devices.FindByIdUnscopedAsync(wantedId, ct)
                : await _devices.FindActiveByNameAsync(wanted, ct);
            if (named is null)
                return Fail(
                    $"not_found: no inventory device named '{wanted}'. An ssh step never opens an "
                    + "ad-hoc connection to an unknown target — register the device first.");

            host = named.IpAddress;
            if (credentialId is null)
                credentialId = named.CredentialId == Guid.Empty ? null : named.CredentialId;
            expectedFingerprint ??= named.ExpectedSshHostKeyFingerprint;
            if (string.IsNullOrWhiteSpace(deviceType) && !string.IsNullOrWhiteSpace(named.Platform))
                deviceType = named.Platform;
        }
        // `host` is an ALIAS carrying an address, and the handler resolves it
        // to an inventory device by IP (snippets/SPEC.md `ssh`). Handed to the
        // runner as written it would open a connection to an arbitrary address
        // with no inventory row behind it: no pinned host-key fingerprint, no
        // environment policy, no audit trail naming a device. No match is
        // `not_found`.
        else if (!request.DeviceId.HasValue && !string.IsNullOrWhiteSpace(host))
        {
            var byIp = await _devices.FindActiveByIpAsync(host!.Trim(), ct);
            if (byIp is null)
                return Fail(
                    $"not_found: no inventory device has the address '{host}'. An ssh step never opens "
                    + "an ad-hoc connection — register the device (or name it with input.device) first.");

            host = byIp.IpAddress;
            if (credentialId is null)
                credentialId = byIp.CredentialId == Guid.Empty ? null : byIp.CredentialId;
            expectedFingerprint ??= byIp.ExpectedSshHostKeyFingerprint;
            if (string.IsNullOrWhiteSpace(deviceType) && !string.IsNullOrWhiteSpace(byIp.Platform))
                deviceType = byIp.Platform;
        }

        if (string.IsNullOrWhiteSpace(host))
            return Fail("cannot resolve SSH host — provide input.host, input.device or a valid device_id");

        string username;
        string? password = null;
        string? privateKeyPem = null;
        string? keyPassphrase = null;

        if (!string.IsNullOrWhiteSpace(inlineUsername))
        {
            // Custom per-run credentials win over input.credential_id and the
            // device's stored credential. Already plaintext — no decryption.
            username = inlineUsername!;
            password = inlinePassword;
            privateKeyPem = inlinePrivateKey;
            keyPassphrase = inlineKeyPassphrase;
            if (string.IsNullOrEmpty(password) && string.IsNullOrWhiteSpace(privateKeyPem))
                return Fail("inline credentials require a password or a private_key");
        }
        else
        {
            if (credentialId is null)
                return Fail("no credential available — provide input.username (with password or private_key), input.credential_id, or a device with a credential");

            var credential = await _credentials.GetByIdAsync(credentialId.Value, tracking: false, ct: ct);
            if (credential is null)
                return Fail($"credential {credentialId} not found");

            try
            {
                username = credential.Username ?? string.Empty;
                var authMethod = string.IsNullOrWhiteSpace(credential.AuthMethod)
                    ? flow_weaver_backend.Models.Credential.AuthMethodPassword
                    : credential.AuthMethod;

                if (string.Equals(authMethod, flow_weaver_backend.Models.Credential.AuthMethodKey,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (credential.EncryptedPrivateKey is null || credential.EncryptedPrivateKey.Length == 0)
                        return Fail("credential AuthMethod=key but no EncryptedPrivateKey is stored");
                    privateKeyPem = _crypto.Decrypt(credential.EncryptedPrivateKey);
                    if (credential.EncryptedKeyPassphrase is { Length: > 0 })
                        keyPassphrase = _crypto.Decrypt(credential.EncryptedKeyPassphrase);
                }
                else
                {
                    password = _crypto.Decrypt(credential.EncryptedPassword);
                }
            }
            catch (Exception ex)
            {
                return Fail($"credential decryption failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(username))
            return Fail("credential has no username");

        // Default device_type if still unresolved. Generic ssh works for
        // Linux-ish targets but won't do prompt/paging tricks — we log
        // so the admin can set Device.Platform to a proper Netmiko
        // device_type (cisco_ios, juniper_junos, …).
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            _logger.LogWarning(
                "worker.ssh.device_type.default step_run_id={StepRunId} device_id={DeviceId} host={Host}",
                request.StepRunId, request.DeviceId, host);
            deviceType = "generic";
        }

        var environment = await ResolveEnvironmentAsync(request.WorkflowRunId, ct);

        // Per-command policy gate. We walk the list in order and stop at
        // the first deny — the runner never sees denied commands. The
        // denial itself lands in `results` at the position it was
        // evaluated, so downstream templates see a faithful timeline.
        var executionPlan = new List<PlannedCommand>();
        foreach (var cmd in commands)
        {
            var decision = await _policies.EvaluateAsync(new PolicyEvaluationContext(
                    Action: "ssh_exec",
                    Environment: environment,
                    WorkflowName: null,
                    WorkflowDescription: null,
                    Nodes: default,
                    DeviceRoles: Array.Empty<string>(),
                    DevicePoolNames: Array.Empty<string>(),
                    SshCommand: cmd),
                ct);
            if (!decision.Allowed)
            {
                executionPlan.Add(PlannedCommand.AsDenied(cmd, decision.PolicyName, decision.Reason));
                break; // hard stop regardless of stop_on_error — policy deny is non-recoverable
            }
            executionPlan.Add(PlannedCommand.AsAllowed(cmd));
        }

        var allowedCommands = executionPlan
            .Where(p => !p.Denied)
            .Select(p => p.Command)
            .ToList();

        // Vendor catalog check at runtime. Mirrors the create/update
        // gate but runs against whatever device_type was actually
        // resolved here (per-node override or Device.Platform). Same
        // policy as plan-time: warnings only — never blocks. The
        // catalog is intentionally non-exhaustive (vendor extensions,
        // custom CLI), so denying unknown commands at runtime would
        // brick legitimate workflows. The findings land in `Logs` so
        // operators see them when reviewing a step_run.
        IReadOnlyList<string> vendorWarnings = Array.Empty<string>();
        if (allowedCommands.Count > 0 && !string.IsNullOrWhiteSpace(deviceType))
        {
            try
            {
                vendorWarnings = await _vendorCommands.ValidateCommandsAsync(deviceType, allowedCommands, ct);
                if (vendorWarnings.Count > 0)
                {
                    _logger.LogWarning(
                        "worker.ssh.vendor_command_warnings step_run_id={StepRunId} device_id={DeviceId} device_type={DeviceType} count={Count}",
                        request.StepRunId, request.DeviceId, deviceType, vendorWarnings.Count);
                }
            }
            catch (Exception ex)
            {
                // The validator is advisory — never abort the step on a
                // catalog lookup failure. Log and proceed.
                _logger.LogWarning(
                    ex, "worker.ssh.vendor_command_validator_failed step_run_id={StepRunId}",
                    request.StepRunId);
            }
        }

        // Build the runner payload. JSON over stdin; plaintext secrets
        // never hit disk/env.
        var runnerInput = JsonSerializer.Serialize(new
        {
            host,
            port,
            username,
            password,
            private_key = privateKeyPem,
            key_passphrase = keyPassphrase,
            enable_secret = enableSecret,
            device_type = deviceType,
            commands = allowedCommands,
            stop_on_error = stopOnError,
            timeout_seconds = timeoutSec,
            use_structured = useStructured,
            preserve_ansi = preserveAnsi,
            direct_exec = directExec,
            use_timing = useTiming,
            read_until_pattern = readUntilPattern,
            setup_commands = setupCommands,
            expected_fingerprint = expectedFingerprint,
        });

        RunnerResult runnerResult;
        if (allowedCommands.Count == 0)
        {
            // Every command was denied — no point spawning Python.
            runnerResult = new RunnerResult(
                Results: new List<RunnerCommandResult>(),
                DeviceType: deviceType,
                HostKeyFingerprint: null,
                FatalError: null);
        }
        else
        {
            runnerResult = await RunNetmikoAsync(runnerInput, timeoutSec, ct);
        }

        if (runnerResult.FatalError is not null)
        {
            _logger.LogWarning(
                "worker.ssh.runner_fatal step_run_id={StepRunId} device_id={DeviceId} host={Host} error={Error}",
                request.StepRunId, request.DeviceId, host, runnerResult.FatalError);
            return Fail(runnerResult.FatalError);
        }

        // Merge policy denials with runner results in the original command order.
        var mergedResults = new List<object>(executionPlan.Count);
        var runnerIndex = 0;
        var anyFailure = false;
        var logsBuilder = new StringBuilder();
        object? lastEntry = null;

        foreach (var plan in executionPlan)
        {
            if (plan.Denied)
            {
                var entry = new
                {
                    command = plan.Command,
                    output = string.Empty,
                    output_raw = string.Empty,
                    parsed = (object?)null,
                    elapsed_ms = 0,
                    ok = false,
                    error = $"blocked by policy `{plan.PolicyName}`: {plan.PolicyReason}",
                    blocked_by_policy = true,
                    policy_name = plan.PolicyName,
                };
                mergedResults.Add(entry);
                logsBuilder.AppendLine($"$ {plan.Command}  [blocked: {plan.PolicyName}]");
                lastEntry = entry;
                anyFailure = true;
                break;
            }

            if (runnerIndex >= runnerResult.Results.Count)
            {
                // Runner returned fewer results than expected (stop_on_error
                // kicked in inside Python). Treat remaining as skipped.
                var skipped = new
                {
                    command = plan.Command,
                    output = string.Empty,
                    output_raw = string.Empty,
                    parsed = (object?)null,
                    elapsed_ms = 0,
                    ok = false,
                    error = "skipped — previous command failed with stop_on_error",
                };
                mergedResults.Add(skipped);
                lastEntry = skipped;
                continue;
            }

            var rc = runnerResult.Results[runnerIndex++];
            var ranEntry = new
            {
                command = rc.Command,
                output = rc.Output,
                output_raw = rc.OutputRaw,
                parsed = rc.Parsed,
                elapsed_ms = rc.ElapsedMs,
                ok = rc.Ok,
                error = rc.Error,
            };
            mergedResults.Add(ranEntry);
            logsBuilder.AppendLine($"$ {rc.Command}  [{(rc.Ok ? "ok" : "fail")} {rc.ElapsedMs}ms]");
            if (!string.IsNullOrEmpty(rc.Output)) logsBuilder.AppendLine(rc.Output);
            lastEntry = ranEntry;
            if (!rc.Ok) anyFailure = true;
        }

        // Back-compat top-level fields pointing at the last command.
        // Legacy templates that read `{{ steps.X.output.exit_code }}`
        // see 0 on full success and 1 on any failure. New templates
        // should prefer `results[i].ok` / `results[i].output`.
        var lastOutput = string.Empty;
        var lastOutputRaw = string.Empty;
        var lastError = string.Empty;
        if (lastEntry is not null)
        {
            var json = JsonSerializer.SerializeToElement(lastEntry);
            if (json.TryGetProperty("output", out var le) && le.ValueKind == JsonValueKind.String)
                lastOutput = le.GetString() ?? string.Empty;
            if (json.TryGetProperty("output_raw", out var lre) && lre.ValueKind == JsonValueKind.String)
                lastOutputRaw = lre.GetString() ?? string.Empty;
            if (json.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                lastError = err.GetString() ?? string.Empty;
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            exit_code = anyFailure ? 1 : 0,
            stdout = lastOutput,
            stdout_raw = lastOutputRaw,
            stderr = lastError,
            host_key_fingerprint = runnerResult.HostKeyFingerprint,
            device_type = runnerResult.DeviceType,
            device_type_fallback = runnerResult.DeviceTypeFallback,
            results = mergedResults,
        });

        if (!string.IsNullOrEmpty(runnerResult.DeviceTypeFallback))
        {
            logsBuilder.Insert(0, $"WARN: {runnerResult.DeviceTypeFallback}{Environment.NewLine}");
        }

        // Surface vendor-catalog findings at the top of the log so they
        // are the first thing an operator reviewing the step_run sees,
        // ahead of the per-command transcript. Each warning is already
        // human-readable from the validator.
        if (vendorWarnings.Count > 0)
        {
            var prefix = new StringBuilder();
            foreach (var w in vendorWarnings)
                prefix.AppendLine($"WARN: {w}");
            logsBuilder.Insert(0, prefix.ToString());
        }

        // Trust-on-first-use. Before this, an unpinned device accepted ANY host
        // key on EVERY connect — the fingerprint was logged and thrown away, so
        // an on-path attacker impersonating the device harvested the credential
        // on any run, not just the first. Persisting the first key observed
        // narrows that to the single first connect and makes every later run
        // fail loudly on a swap.
        await TryAutoPinAsync(request.DeviceId, host, expectedFingerprint, runnerResult.HostKeyFingerprint, ct);

        _logger.LogInformation(
            "worker.ssh.done step_run_id={StepRunId} device_id={DeviceId} host={Host} device_type={DeviceType} commands={Commands} failures={Failures}",
            request.StepRunId, request.DeviceId, host, runnerResult.DeviceType,
            mergedResults.Count, anyFailure ? 1 : 0);

        return new SnippetResult
        {
            Success = !anyFailure,
            Output = output,
            // The commands come from the node, so the same snippet runs `show version` on
            // one node and `configure terminal` on another. Only the author can tell those
            // apart, and `config_overrides.changes` is where they say so.
            Change = StepChange.AuthorDecides,
            Logs = logsBuilder.ToString().TrimEnd(),
            Error = anyFailure ? (lastError.Length > 0 ? lastError : "one or more commands failed") : string.Empty,
        };
    }

    // Pins the observed host key on a device that has none yet (TOFU).
    //
    // Skipped when: the step had no device row (ad-hoc host), the device was
    // already pinned (the runner enforced it — reaching here means it matched),
    // the runner reported no key (every command was policy-denied, or a
    // fatal), or the operator disabled auto-pinning.
    //
    // Ssh:AutoPinHostKeyOnFirstUse (env Ssh__AutoPinHostKeyOnFirstUse) defaults
    // to true. Turn it off only where devices legitimately re-key between runs
    // (some virtual labs rebuild the box each time) — the cost is that those
    // devices stay open to key substitution until an operator pins them by hand.
    private async Task TryAutoPinAsync(
        Guid? deviceId, string host, string? expectedFingerprint, string? observed, CancellationToken ct)
    {
        if (deviceId is not { } id) return;
        if (!string.IsNullOrEmpty(expectedFingerprint)) return;
        if (string.IsNullOrWhiteSpace(observed)) return;

        if (!_configuration.GetValue("Ssh:AutoPinHostKeyOnFirstUse", true))
        {
            _logger.LogWarning(
                "ssh.host_key.unpinned device_id={DeviceId} host={Host} fingerprint={Fingerprint} "
                + "— auto-pin is off (Ssh:AutoPinHostKeyOnFirstUse=false); this device accepts any host key. "
                + "Pin it via PATCH /api/device/{DeviceId} expected_ssh_host_key_fingerprint.",
                id, host, observed, id);
            return;
        }

        // Store the canonical form so a later operator edit compares equal.
        if (!SshHostKeyFingerprint.TryNormalize(observed, out var normalized, out var error))
        {
            _logger.LogWarning(
                "ssh.host_key.pin_skipped device_id={DeviceId} host={Host} reason={Reason}",
                id, host, error);
            return;
        }

        try
        {
            var pinned = await _devices.TryPinHostKeyAsync(id, normalized!, ct);
            if (pinned)
                _logger.LogInformation(
                    "ssh.host_key.pinned device_id={DeviceId} host={Host} fingerprint={Fingerprint} "
                    + "— trust-on-first-use; later connects presenting a different key now fail.",
                    id, host, normalized);
        }
        catch (Exception ex)
        {
            // Never fail a successful step because bookkeeping failed. The next
            // run retries the pin.
            _logger.LogWarning(ex,
                "ssh.host_key.pin_failed device_id={DeviceId} host={Host}", id, host);
        }
    }

    // A `${secret:...}` reference resolved to its plaintext. Anything without
    // a marker is returned untouched (the common case, and the one that must
    // not hit the secret store); an unresolvable marker is left literal by the
    // resolver, which surfaces as an auth failure naming the reference rather
    // than as a silent empty password.
    private async Task<string?> ResolveSecretsAsync(string? value, CancellationToken ct)
        => string.IsNullOrEmpty(value) || !value.Contains("${secret:", StringComparison.Ordinal)
            ? value
            : await _secrets.SubstituteAsync(value, ct);

    private async Task<string> ResolveEnvironmentAsync(Guid workflowRunId, CancellationToken ct)
    {
        var env = await _runs.GetEnvironmentByRunIdAsync(workflowRunId, ct);
        return string.IsNullOrWhiteSpace(env) ? "draft" : env;
    }

    // Exposed for a test that pins the guard below: the normaliser now drops the redundant
    // alias upstream, and this is what keeps a payload that never went through it safe.
    internal static List<string> ResolveCommandsForTest(JsonElement input, out string? error)
        => ResolveCommands(input, out error);

    private static List<string> ResolveCommands(JsonElement input, out string? error)
    {
        error = null;

        var hasSingle = input.TryGetProperty("command", out var singleEl)
                        && singleEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(singleEl.GetString());
        var hasBatch = input.TryGetProperty("commands", out var batchEl)
                       && batchEl.ValueKind == JsonValueKind.Array;

        // bundle/SPEC.md §4: when both the canonical key and its alias are
        // present, the CANONICAL one wins. `commands` is canonical and
        // `command` is the single-string alias, so a node carrying both is a
        // node with a leftover key — not an error, and certainly not a reason
        // to refuse a step whose meaning the standard defines.
        if (hasSingle && hasBatch) hasSingle = false;
        if (!hasSingle && !hasBatch)
        {
            error = "input.command (string) or input.commands (array) is required";
            return new();
        }

        if (hasSingle)
            return new() { singleEl.GetString()! };

        var list = new List<string>();
        foreach (var el in batchEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var cmd = el.GetString();
            if (!string.IsNullOrWhiteSpace(cmd))
                list.Add(cmd!);
        }
        if (list.Count == 0)
        {
            error = "input.commands must contain at least one non-empty string";
            return new();
        }
        return list;
    }

    // Spawns the Python runner, pipes the JSON payload on stdin, waits
    // for JSON on stdout. We keep the whole thing in-memory (MemoryStream
    // for stdin write, StringBuilder for stdout/stderr reads) so nothing
    // containing plaintext credentials ever touches disk. Timeout is the
    // step's `timeout_seconds` + a 10s grace for Python/netmiko import
    // and teardown; if Paramiko hangs the CancellationToken kills the
    // process tree.
    private async Task<RunnerResult> RunNetmikoAsync(string payload, int timeoutSec, CancellationToken ct)
    {
        var exe = _configuration["Python:Executable"];
        if (string.IsNullOrWhiteSpace(exe)) exe = DefaultPythonExecutable;

        var runner = _configuration["Python:SshRunnerPath"];
        if (string.IsNullOrWhiteSpace(runner)) runner = DefaultRunnerPath;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(runner);

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new RunnerResult(
                Results: new(),
                DeviceType: null,
                HostKeyFingerprint: null,
                FatalError: $"could not spawn Python runner ({exe} {runner}): {ex.Message}");
        }

        // Grace: Netmiko's Paramiko import + first handshake can take
        // several seconds cold, especially for large keys. The Python
        // side already clamps command reads to `timeout_seconds`; this
        // outer timeout is only to guard against a totally stuck process.
        var processTimeout = TimeSpan.FromSeconds(timeoutSec + 30);
        using var procCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        procCts.CancelAfter(processTimeout);

        try
        {
            await proc.StandardInput.WriteAsync(payload);
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            TryKill(proc);
            return new RunnerResult(new(), null, null, $"failed to write runner payload: {ex.Message}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(procCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(procCts.Token);

        try
        {
            await proc.WaitForExitAsync(procCts.Token);
        }
        catch (OperationCanceledException) when (procCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKill(proc);
            return new RunnerResult(new(), null, null, $"SSH runner exceeded {processTimeout.TotalSeconds:0}s wall-clock deadline");
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        string stdout, stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (Exception ex)
        {
            return new RunnerResult(new(), null, null, $"failed to read runner output: {ex.Message}");
        }

        if (proc.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? $"runner exited {proc.ExitCode}" : stderr.Trim();
            return new RunnerResult(new(), null, null, msg);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new RunnerResult(new(), null, null, "runner returned empty output");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var parsed = new List<RunnerCommandResult>();
            if (root.TryGetProperty("results", out var resEl) && resEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in resEl.EnumerateArray())
                {
                    parsed.Add(new RunnerCommandResult(
                        Command: r.TryGetProperty("command", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() ?? string.Empty : string.Empty,
                        Output: r.TryGetProperty("output", out var oEl) && oEl.ValueKind == JsonValueKind.String ? oEl.GetString() ?? string.Empty : string.Empty,
                        OutputRaw: r.TryGetProperty("output_raw", out var orEl) && orEl.ValueKind == JsonValueKind.String ? orEl.GetString() ?? string.Empty : string.Empty,
                        Parsed: r.TryGetProperty("parsed", out var pEl) && pEl.ValueKind != JsonValueKind.Null && pEl.ValueKind != JsonValueKind.Undefined
                                    ? JsonSerializer.Deserialize<object>(pEl.GetRawText())
                                    : null,
                        ElapsedMs: r.TryGetProperty("elapsed_ms", out var eEl) && eEl.TryGetInt32(out var ev) ? ev : 0,
                        Ok: r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True,
                        Error: r.TryGetProperty("error", out var erEl) && erEl.ValueKind == JsonValueKind.String ? erEl.GetString() : null));
                }
            }
            var devType = root.TryGetProperty("device_type", out var dtEl) && dtEl.ValueKind == JsonValueKind.String ? dtEl.GetString() : null;
            var fp = root.TryGetProperty("host_key_fingerprint", out var fpEl) && fpEl.ValueKind == JsonValueKind.String ? fpEl.GetString() : null;
            var fallback = root.TryGetProperty("device_type_fallback", out var fbEl) && fbEl.ValueKind == JsonValueKind.String ? fbEl.GetString() : null;
            if (!string.IsNullOrEmpty(fallback))
            {
                _logger.LogWarning("worker.ssh.device_type_fallback message={Message}", fallback);
            }
            return new RunnerResult(parsed, devType, fp, FatalError: null, DeviceTypeFallback: fallback);
        }
        catch (Exception ex)
        {
            return new RunnerResult(new(), null, null, $"runner output parse failed: {ex.Message}. raw={Truncate(stdout, 300)}");
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };

    private sealed record PlannedCommand(string Command, bool Denied, string? PolicyName, string? PolicyReason)
    {
        public static PlannedCommand AsAllowed(string command) => new(command, false, null, null);
        public static PlannedCommand AsDenied(string command, string? policy, string? reason) =>
            new(command, true, policy, reason);
    }

    private sealed record RunnerResult(
        List<RunnerCommandResult> Results,
        string? DeviceType,
        string? HostKeyFingerprint,
        string? FatalError,
        string? DeviceTypeFallback = null);

    private sealed record RunnerCommandResult(
        string Command,
        string Output,
        string OutputRaw,
        object? Parsed,
        int ElapsedMs,
        bool Ok,
        string? Error);
}
