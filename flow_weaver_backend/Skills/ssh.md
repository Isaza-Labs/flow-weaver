# Skill: ssh — run commands on a device (Netmiko)

The `ssh` snippet is seeded on first boot. Look up its UUID with
`fw_snippets:list_snippets(type="ssh")` and reference it in a node's
`snippet_id`. The literal string `"ssh"` is NOT a sentinel — the
executor rejects it.

**Backend**: Netmiko (Python). The C# handler spawns
`/usr/local/lib/flow_weaver_ssh_runner.py`, pipes a JSON payload, parses
the JSON response. The runner ships in the image; it can't be edited.

Why Netmiko: vendor-aware CLI (prompt detection, paging disablement,
enable mode, TextFSM), 40+ `device_type` values out of the box,
per-command timeout, clean disconnect.

## Defaults

| Field | Default | Notes |
|---|---|---|
| `target_mode` | `per_device` | Workflow needs `target_devices` / `target_pools`. |
| `timeout_seconds` | `30` | Clamped `[5, 300]`. Outer process deadline = `timeout_seconds + 30s`. |
| `port` | `22` | |
| `device_type` | `Device.Platform` → `"generic"` | See below. |
| auth | stored `Credential` (`password`/`key`) — or inline `username` + secret | Inline credentials override the stored credential. See [Authentication](#authentication). |

## Command decision protocol (read first)

The #1 cause of a dead SSH node is transposing IOS-style syntax onto a
non-IOS vendor — `show lldp neighbors` on `nokia_srl`, `show ip bgp` on
`nokia_sros`, `show interface` on `huawei`. The model's pre-trained guess
is trustworthy **only** for `cisco_ios` and `linux`. For every other
`device_type`, treat the command as something you *retrieve from the
catalog*, not something you invent.

Run this order for any ssh node, before `create_workflow` /
`update_workflow`:

1. **Resolve the target's `device_type` and capabilities.** Call
   `query_devices` to read each target's `platform` — that sets the
   `device_type` (see § `device_type` resolution for the full override
   order) — plus `os_version`, `role` and `model` when the inventory
   carries them. Use `os_version`/`model` to pick version- and
   model-specific syntax (e.g. IOS vs IOS-XE, 16.x vs 17.x), and `role`
   to sanity-check the intent against the device's job. If targets span
   more than one platform, plan per-platform — one command set does not
   fit all.
2. **Restate the task as a vendor-neutral intent**: "show LLDP
   neighbors", "dump running-config", "show BGP summary". You map the
   intent → command, never the reverse.
3. **Retrieve the command from the catalog, not from memory** — unless
   `device_type ∈ {cisco_ios, linux}`. Call
   `find_command(device_type, task)` with the intent from step 2; it
   returns the ranked canonical command(s) for that vendor, each with its
   purpose and risk (`read`/`write`/`disruptive`). Fall back to
   `list_vendor_commands(device_type)` to browse the whole catalog when
   the match is weak. The verb shape differs per vendor; never translate
   an IOS command 1:1. See the Nokia pitfalls below for what that looks
   like.
4. **Validate before saving.** Always finish with
   `validate_ssh_commands(device_type, commands=[...])` and fix every
   warning. If the catalog genuinely lacks a real command, propose adding
   it via `/api/vendor-command` — do not ship an unvalidated guess.

Do not jump straight to step 4 (guess, then validate): the validator's "did you mean" is Levenshtein, so it
catches typos, not wrong-dialect commands. `show lldp neighbors` →
`show system lldp neighbor` is too far apart to be suggested, so the
guess fails silently with an echoed command and no output.

## `config_overrides` shape

```json
{
  "command":         "show ip interface brief",
  "commands":        ["show ver", "show run"],
  "stop_on_error":   true,
  "host":            "10.0.0.5",
  "port":            22,
  "timeout_seconds": 30,
  "credential_id":   "<uuid>",
  "username":        "<inline user — overrides credential_id/device cred>",
  "password":        "<inline password>",
  "private_key":     "<inline PEM key — alternative to password>",
  "key_passphrase":  "<optional passphrase for private_key>",
  "device_type":     "cisco_ios",
  "enable_secret":   "...",
  "use_structured":  false
}
```

`username`/`password`/`private_key`/`key_passphrase` are the inline
("custom") credential override — when `username` is present they take
precedence over `credential_id` and the device's credential. Read the
at-rest caveat in [Authentication](#authentication) before putting them
in a committed `config_overrides`.

## `device_type` resolution

1. `config_overrides.device_type` (per-node).
2. `Device.Platform` (per device, set from UI / NetBox sync).
3. `"generic"` — works for plain Linux, but NO prompt detection or
   paging disablement; runner logs a warning.

| Vendor / OS | `device_type` |
|---|---|
| Cisco IOS / IOS-XE | `cisco_ios`, `cisco_xe` |
| Cisco IOS-XR | `cisco_xr` |
| Cisco NX-OS | `cisco_nxos` |
| Juniper Junos | `juniper_junos` |
| Arista EOS | `arista_eos` |
| Nokia SR OS (classic 7750/7250 routers, TiMOS CLI) | `nokia_sros` |
| Nokia SR Linux (7220 IXR, 7250 IXR-DC, containerlab `ghcr.io/nokia/srlinux`) | `nokia_srl` |
| Huawei | `huawei` |
| Fortinet | `fortinet` |
| Linux shell | `linux` |
| Catch-all | `generic` |

Full matrix: https://github.com/ktbyers/netmiko (PLATFORMS.md).

**Common confusion**: `nokia_sros` and `nokia_srl` are **different products
with different CLIs** — they are NOT interchangeable. SR OS uses TiMOS
(`A:node# show router ...`); SR Linux uses an info-tree CLI
(`info from state ...`, plus `show version` aliases). Picking the wrong
one makes Netmiko's prompt regex misfire and the runner returns only
the echoed command. If the device's docker image is
`ghcr.io/nokia/srlinux` or the `Device.Vendor` is "Nokia SR Linux", use
`nokia_srl`. If unsure, set the alias `srlinux` or `sros` — the runner
normalizes both via `VENDOR_ALIASES`.

### Nokia SR Linux command shape — DON'T copy IOS

SR Linux's CLI rejects IOS-style top-level shortcuts. Only these
families exist at the prompt root: `acl`, `arpnd`, `interface`, `lag`,
`network-instance`, `platform`, `system`, `tunnel`, `tunnel-interface`,
`version`. Anything else returns `Unknown token 'X'`.

Concretely the agent must **never** propose these for `nokia_srl`:

| Don't write              | Use instead                              |
| ------------------------ | ---------------------------------------- |
| `show lldp neighbors`    | `show system lldp neighbor` (singular!)  |
| `show ip route`          | `show network-instance default route-table summary` |
| `show running-config`    | `info` (or `info from running …`)        |
| `show vlan`              | `show network-instance default …`        |
| `show interfaces status` | `show interface brief`                   |
| `show mac address-table` | `show network-instance default bridge-table` |

Same trap for `nokia_sros` — the TiMOS CLI uses `show router … bgp neighbor`, NOT `show ip bgp summary`. This is the **Command decision protocol** in action: for **any** `device_type` outside `cisco_ios`/`linux` — Nokia, Juniper, Huawei, Fortinet, Arista, F5, PAN-OS — run `list_vendor_commands(device_type=…)` BEFORE drafting and `validate_ssh_commands(commands=[...])` after. The Nokia and Juniper families just punish a blind guess the hardest.

**Auto-set `use_structured: true` whenever the workflow consumes any
parsed field downstream** (`{{ steps.X.output.results[0].parsed[...] }}`,
NetBox custom_fields holding a software version, etc.). Without it,
`parsed` is `null` and the template reference resolves to nothing.
The runner's parser pipeline (TextFSM local + upstream + generic
key-value fallback) covers Nokia SR Linux, Cisco IOS/XE/XR, Arista,
Juniper, and falls back gracefully on unknown commands.

## Output shape

```json
{
  "exit_code": 0,
  "stdout": "...",         // ANSI-stripped (CSI/OSC/control chars removed)
  "stdout_raw": "...",     // untouched bytes from Netmiko (for forensics)
  "stderr": "",
  "host_key_fingerprint": "SHA256:...",
  "device_type": "cisco_ios",
  "results": [
    { "command": "show version", "output": "...", "output_raw": "[0m...",
      "parsed": null, "elapsed_ms": 412, "ok": true, "error": null },
    { "command": "reload", "output": "", "output_raw": "", "parsed": null,
      "elapsed_ms": 0, "ok": false, "error": "blocked by policy `no-destructive`",
      "blocked_by_policy": true, "policy_name": "no-destructive" }
  ]
}
```

Downstream templates:

```
{{ steps.<node>.output.results[0].output }}      # ANSI-stripped text
{{ steps.<node>.output.results[0].output_raw }}  # original bytes incl. ANSI
{{ steps.<node>.output.results[0].parsed }}      # list[dict] when TextFSM matched
{{ steps.<node>.output.host_key_fingerprint }}   # to pin after first-seen
```

`exit_code` and `stdout` are synthesized from the last command for
back-compat. New workflows should prefer the `results[]` array — per-
command `ok` is finer than the coarse exit code.

### ANSI stripping

The runner sanitises every command's output before returning it: ANSI
CSI escape sequences (`\x1b[0m`, `\x1b[23D`, etc.), OSC payloads, and
C0 control chars (except `\t \n \r`) are stripped from `output`. This
matters for vendors that always emit ANSI — Nokia SR-OS, Cisco IOS-XR
with paging, anything wrapped by `screen` — where the raw bytes would otherwise
contaminate downstream consumers (NetBox custom_fields, email bodies,
Slack messages) with cursor-move codes that render as garbage.

Disable per-node by setting `preserve_ansi: true` in the SSH step's
`config_overrides`. The raw output is always available under
`output.stdout_raw` and `results[i].output_raw` regardless.

### Transport mode: Netmiko vs direct exec

Two ways the runner can talk to a device:

| Mode                         | When                                                                                | How                                                                                                                       |
| ---------------------------- | ----------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| **Netmiko (default)**        | Most vendors: cisco_ios / cisco_xe / cisco_xr / juniper_junos / arista_eos / nokia_sros / huawei / fortinet / linux / generic. | Interactive shell with PTY, vendor-aware prompt detection, paging disabled, optional TextFSM. |
| **direct exec (`direct_exec`)** | Auto-true for `device_type: nokia_srl`. Opt-in for any other type via `direct_exec: true`. | One SSH `exec` channel per command, no PTY. Equivalent to `ssh user@host "cmd"`.        |

**Why direct exec exists.** SR Linux's interactive CLI emits an
operational banner (`--{ running }--[ ]--`) and uses cursor-redraw
escapes (`\x1b[23D`) around the command echo. Netmiko's prompt-regex
reader sees the prompt match in the echoed line and terminates the read
**before** the actual command output arrives. None of the netmiko-mode
escapes (`setup_commands`, `use_timing`, `read_until_pattern`) fix this
because the issue is structural: the interactive CLI is talking to a
human-style terminal, and Netmiko is reading like a script. Direct exec
asks the SSHd for a non-interactive channel; SR Linux switches to batch
mode and outputs cleanly.

The output schema is **identical** between modes — `results[]`,
`stdout`, `stdout_raw`, `stderr`, `exit_code`, `host_key_fingerprint`,
`device_type`. Templates that work today against `output.results[0]
.output` keep working.

**Security primitives are unchanged.** Both paths reuse the same
`PinPolicy` for SHA256 host-key pinning, the same credential decryption
pipeline, the same Policy gate (commands are gated in C# before the
runner is even spawned), the same ANSI strip, the same timeouts. The
only behavioral difference: direct_exec does NOT allocate a PTY, which
is strictly more restrictive.

**Knobs that are netmiko-only and silently ignored in direct_exec:**
`setup_commands`, `use_timing`, `read_until_pattern`, `enable_secret`,
`use_structured` (TextFSM). These are interactive-shell concepts with no
analogue in the exec model.

**Multi-command sessions.** In Netmiko all commands share one shell
session. In direct_exec each command is a separate SSH `exec` channel
on the same TCP connection — `terminal length 0` on command 1 does NOT
affect command 2. SR Linux doesn't need this; for vendors that do, leave
`direct_exec: false`.

```json
// SR Linux — auto direct_exec, just type the command and ship it.
"config_overrides": {
  "command": "show version",
  "device_type": "nokia_srl",
  "timeout_seconds": 30
}

// Force Netmiko on SR Linux (rare; only if direct_exec mishandles
// some edge case for your topology).
"config_overrides": {
  "command": "show version",
  "device_type": "nokia_srl",
  "direct_exec": false
}

// Force direct_exec on Cisco (rare; useful when paging or banners
// from a customised CLI break Netmiko's reader).
"config_overrides": {
  "command": "show version",
  "device_type": "cisco_ios",
  "direct_exec": true
}
```

### Netmiko-mode quirks: prompt detection escapes

Only relevant when **not** running in direct_exec mode. Three opt-in
escapes in `config_overrides`:

| Field                   | Effect                                                                                                                                    |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `setup_commands: [...]` | List of CLI commands sent before `commands`; their output is discarded. Used to disable paging or switch CLI mode.                        |
| `use_timing: true`      | Switches to `send_command_timing()` — delay-based reads instead of prompt-regex. Slower but immune to prompt detection bugs. No TextFSM. |
| `read_until_pattern`    | Regex passed as `expect_string` to override Netmiko's prompt detection per command (e.g. `r"#\s*$"`).                                     |

For Junos / IOS-XE / IOS-XR, paging suppression usually only needs
`setup_commands: ["set cli screen-length 0"]` (Junos) or
`["terminal length 0"]` (Cisco) — Netmiko already does this for known
platforms but it's a useful override when `device_type` falls back to
`generic`.

## Authentication

Credentials resolve in this precedence order: **inline → `credential_id`
→ device credential.**

### 1. Stored `Credential` (recommended)

Reference a stored credential via `config_overrides.credential_id`, or
let the handler fall back to the target `Device.CredentialId`. Encrypted
at rest; the auth method is driven by `Credential.AuthMethod`:

- **`password`** (default): `Credential.EncryptedPassword`.
- **`key`**: `Credential.EncryptedPrivateKey` + optional
  `Credential.EncryptedKeyPassphrase`. The runner tries
  Ed25519 → ECDSA → RSA → DSS. Validation at `POST /api/credential` only
  checks PEM shape; deep parsing happens at connect time.

### 2. Inline ("custom") credentials

Pass credentials directly on the step input / `config_overrides`. When
`username` is present it becomes the credential source — **no stored
Credential row is needed**, and it overrides both `credential_id` and the
device's credential:

| Field | Notes |
|---|---|
| `username` | Required to activate inline mode. |
| `password` | Password auth. Provide this **or** `private_key`. |
| `private_key` | PEM key for key-based auth (alternative to `password`). |
| `key_passphrase` | Optional passphrase for `private_key`. |

With `username` set but neither `password` nor `private_key`, the step
fails fast: `inline credentials require a password or a private_key`.
Host, host-key fingerprint and `device_type` still come from the target
Device — only the credentials are swapped.

When the user hands you a one-off `username` + `password` (or `private_key`)
for a device, set them on the ssh node directly — you do NOT need to ask them
to register a Credential first. (Mind the at-rest caveat below for anything
you persist.)

**Security caveat — inline secrets are NOT encrypted at rest.** Unlike a
stored `Credential`, anything you put here lives in plaintext in the
workflow definition (when placed in `config_overrides`) and in the
run/step input payload, and may surface in audit/trace. Use inline creds
for **ad-hoc runs or run-time-injected** credentials from a secure
source; for anything committed or long-lived, use a stored `Credential`
(option 1). The SSH handler itself sends them to the runner over stdin
and never logs them, but it cannot un-persist what the editor/run stored.

## Host-key pinning

When `Device.ExpectedSshHostKeyFingerprint` is set
(`SHA256:<base64>`, trailing `=` optional), the runner compares at
connect. Mismatch → step fails with
`host key mismatch for <host>: expected X, got Y`.

When null: trust on first use. The connect accepts the presented key and
reports it as `host_key_fingerprint`; for a step that targets a device
row, that fingerprint is then pinned on the device automatically, so later
connects presenting a different key fail. An existing pin is never
overwritten, and ad-hoc hosts (no device row) are never pinned. Admins can
turn this off (`Ssh:AutoPinHostKeyOnFirstUse=false`, for labs that re-key
between runs); unpinned devices then stay unpinned until someone sets
`expected_ssh_host_key_fingerprint` (device edit dialog or
`fw_inventory:update_device`, i.e. `PUT /api/device/{id}`). After a
legitimate re-key, setting it to `""` lets the next connect re-pin.

## Plan-time command validation (`vendor_commands` catalog)

The `vendor_commands` table is seeded with a baseline of
known-good CLI commands per `device_type` (cisco_ios, juniper_junos,
nokia_srl, etc.). Two validators consume it:

1. **`WorkflowService` create/update** — every ssh node's
   `config_overrides.command` / `commands` is checked against the
   catalog for the resolved `device_type`. Unknown commands return as
   **warnings** in the workflow response (never block). Resolution
   order: per-node `config_overrides.device_type` → fallback "validation
   deferred". When the catalog has no entries for a `device_type`, the
   warning says so and the agent should propose adding entries via
   `/api/vendor-command` (or the `/vendor-commands` admin UI).
2. **`validate_ssh_commands` AI tool** — call this BEFORE
   `create_workflow_plan` with `{ device_type, commands: [...] }`. The
   tool returns:
   ```json
   {
     "valid": false,
     "device_type": "cisco_ios",
     "commands_checked": 2,
     "warnings": ["command[1] 'show vesion' is not a known command for device_type 'cisco_ios'. Did you mean: 'show version'? ..."],
     "known_commands": ["show version", "show running-config", ...],
     "catalog_total": 27
   }
   ```
   `known_commands` is a sample (up to 30) the agent can use to suggest
   replacements when the user mistyped a command.

Catalog rows have `Kind = "exact"` (literal match, case-insensitive,
whitespace-collapsed) or `Kind = "pattern"` (regex evaluated against the
normalised command, 250 ms timeout). The validator first tries the
exact-match set, then walks patterns. A pattern like
`^show\s+\S+(\s+\S+)*$` covers every `show ...` form so the catalog
doesn't have to enumerate them all.

The catalog is **never** an authorisation gate — destructive verbs
still go through `Policy.ssh_command_regex` (below). Vendor commands
are about "is this a real command for this OS", not "is this allowed".

## Per-command policy

Destructive verbs are blocked with a Policy row. `PolicyEvaluator`
runs BEFORE each command — denied commands never reach the runner.

```json
{
  "action": "deny",
  "reason": "Destructive verbs need a change window",
  "when": {
    "env": ["qa", "production"],
    "ssh_command_regex": ["^\\s*(reload|write\\s+erase|delete|format\\s+flash)"]
  }
}
```

Regex is case-insensitive, 250ms per-pattern timeout. Match → command
shows in `results[]` with `blocked_by_policy=true`, and **every
subsequent command is skipped** regardless of `stop_on_error` (policy
denial is non-recoverable).

## Canonical workflow — "backup every core router"

```
__start__ → [ssh-backup] → [persist-report] → __end__

ssh-backup (target_mode=per_device):
  config_overrides:
    device_type: "cisco_ios"
    commands:
      - "terminal length 0"
      - "show running-config"
    stop_on_error: true
    timeout_seconds: 60

persist-report (target_mode=once):
  config_overrides:
    format: "html"
    document:
      title: "Running-config snapshot"
      sections:
        - title: "Running config"
          tables:
            - headers: ["Device", "Status", "Config"]
              rows:
                - - "{{ steps.ssh-backup.output.devices[0].device }}"
                  - "{{ steps.ssh-backup.output.devices[0].status }}"
                  - "{{ steps.ssh-backup.output.devices[0].results[1].output }}"
```

`persist-report` is `once` and its producer is `per_device`, so it reads
`output.devices[N]`, not `output.results`. Sections carry `title`,
`tables` and `callouts`.

## Structured output (`use_structured`)

`use_structured: true` enables Netmiko's `use_textfsm=True`. When a
TextFSM template matches the (vendor, command) pair, `results[i].parsed`
becomes a list of dicts:

```
config_overrides: { device_type: "cisco_ios", command: "show ip interface brief", use_structured: true }

→ steps.X.output.results[0].parsed = [
    { "intf": "Gi0/0", "ipaddr": "10.0.0.1", "status": "up", "proto": "up" }, ...
  ]
```

No template match → `parsed` is `null`, `output` keeps the raw string.
Templates use it defensively:

```
{% if steps.X.output.results[0].parsed %}
  {{ steps.X.output.results[0].parsed[0].ipaddr }}
{% else %}
  {{ steps.X.output.results[0].output }}
{% endif %}
```

## Gotchas

- **`device_type` matters.** `generic` on a Cisco works for plain
  `send_command` but breaks `send_config_set` and `enable()`.
- **Enable on Cisco/Arista**: set `enable_secret` on the node config OR
  in the credential's Extra JSON. The runner only triggers `enable()`
  when `enable_secret` is present on input.
- **TextFSM coverage is partial** — `parsed=null` on unsupported
  commands is normal; treat structured output as best-effort.
- **No interactive prompts** other than `enable`. A command awaiting
  `[Y/N]` will time out — use vendor `commit confirmed` / `force` flags,
  or build a `send_command_timing` wrapper in a `python_snippet`.

## When NOT to use `ssh`

- Device exposes a REST API → prefer `integration_action`. Structured
  output beats CLI parsing. (The `netconf` snippet type is a stub that
  fails at run time; don't use it.)
- Idempotent config push to many devices → prefer `ansible_playbook`.
- Multi-step workflows with commit/rollback semantics → write a
  `python_snippet` that owns the Netmiko session and implements
  rollback. The seeded `ssh` snippet tears down the session at each
  step boundary, so cross-step rollback is awkward.
