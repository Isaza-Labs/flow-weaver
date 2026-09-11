# Network-enabled python snippets (interactive SSH with netmiko/paramiko)

A `python_snippet` normally runs in a sandbox with **no network**. Setting
`network_enabled: true` (admin-only) lifts that for ONE snippet so the script can
open SSH with **netmiko**/**paramiko** — for interactive flows the fixed `ssh`
step can't do.

## When to use this (and when NOT to)

**Default to the `ssh` step.** It already runs netmiko with vendor-aware parsing
(TextFSM/ntc-templates), host-key pinning, credential handling, and policy
checks. Use it for normal "send command(s), read output".

**Reach for a network-enabled `python_snippet` ONLY when the interaction needs
prompt/response logic the `ssh` step can't express**, e.g.:

- A **password change that prompts for confirmation** ("New password:" →
  "Retype/Confirm password:").
- **commit-confirm / "are you sure? [y/n]"** style prompts mid-session.
- Multi-step **expect** flows, or **branching by `device_type`** inside one
  live connection.

If a plain `ssh` step covers it, do NOT use this — it runs with far fewer
guardrails.

## Start from the seeded baseline, not from scratch

A baseline snippet named **`SSH primitive (paramiko)`** (`type: python_snippet`,
`network_enabled: true`, `target_mode: per_device`) ships with every install. It
already does both shapes on one connection:

- `mode: "exec"` (default) — each entry of `commands` on its own channel, with
  `stdout` / `stderr` / `exit_status` per command.
- `mode: "shell"` — one interactive shell driven by `steps`, each
  `{send, expect}`. That is the mode for a prompt.

Point a node at it and configure it through `config_overrides` before writing
anything new. A step carrying a secret takes `"secret": true`, which keeps its
payload out of the stored run output. Three details in that body are easy to lose
on a rewrite, so copy them rather than reinventing:

- `allow_agent=False, look_for_keys=False` — otherwise paramiko silently tries
  the WORKER's own ssh-agent and `~/.ssh` keys, and a workflow reaches a device
  nobody ever gave it a credential for.
- no `re.compile(...)`: the import guard blocks the name `compile` wherever it
  appears, attribute access included. Use the module-level `re.search` / `re.sub`.
- no helper whose name ends in `exec(`: the pre-filter matches substrings, so
  `_run_exec(` is refused while `client.exec_command(` is fine.

Write a fresh one only when the interaction genuinely does not fit that shape.

## How to create one

Call `fw_snippets:create_snippet` with `type: "python_snippet"` and
`network_enabled: true`. **This requires the admin role.** If your call is
rejected with `network_enabled requires the admin role`, you are not running as
an admin — tell the user this snippet must be created by an admin, and stop.
`logic_diagram_mermaid` is still required (as for any python_snippet).

## Runtime contract (same as python_snippets, plus network)

- Read input with `get_input()`, return with `set_output(...)` (from
  `flowweaver_runtime`).
- Device metadata is in `inp["device"]`: `.ip`, `.platform`, `.vendor`, `.name`.
  **Credentials are NOT auto-injected** — pass them through `config_overrides`
  using secret placeholders the resolver substitutes before the script runs:
  `"${secret:credential:<credential_id>:username}"` and `:password` / `:private_key`.
- **Allowed imports (network-enabled set):** `netmiko`, `paramiko`, `socket`,
  `time`, `select`, `textfsm`, `ntc_templates`, `logging` — plus the base safe
  modules (`json`, `re`, `datetime`, `ipaddress`, …). Still **blocked**:
  `exec`/`eval`/`open`/`__import__`, `os`, `subprocess`, `requests`/`urllib`.
- Map `inp["device"]["platform"]` to a netmiko `device_type`
  (`cisco_ios`, `cisco_xr`, `cisco_nxos`, `juniper_junos`, `arista_eos`,
  `nokia_sros`, `nokia_srl`, `huawei`, `fortinet`, `linux`, …). Let the author
  pass an explicit `device_type` in config_overrides too.
- Always `disconnect()` in a `finally`. Keep the snippet within its
  `timeout_seconds` (5–600).

## Example: change a password that prompts for confirmation

`input_schema` (config_overrides) should declare: `device_type`, `username`,
`password`, `new_password`. Wire credentials with secret placeholders.

```python
from netmiko import ConnectHandler
from flowweaver_runtime import get_input, set_output, log

inp = get_input()
dev = inp["device"]                       # injected device context

conn = ConnectHandler(
    device_type=inp.get("device_type", "cisco_ios"),
    host=dev["ip"],
    username=inp["username"],             # "${secret:credential:<id>:username}"
    password=inp["password"],             # "${secret:credential:<id>:password}"
    fast_cli=False,
)
transcript = ""
try:
    new_pw = inp["new_password"]
    # send_command_timing() doesn't wait for the normal prompt — it returns
    # whatever the device printed, so we can answer each interactive prompt.
    transcript += conn.send_command_timing("set password")   # adjust per vendor
    if "ew password" in transcript:                          # "New password:"
        transcript += conn.send_command_timing(new_pw)
    if "etype" in transcript or "onfirm" in transcript:      # "Retype"/"Confirm"
        transcript += conn.send_command_timing(new_pw)
    ok = "success" in transcript.lower() or "changed" in transcript.lower()
    log("info", "password change attempted", device=dev["name"], ok=ok)
    set_output({"ok": ok, "device": dev["name"], "transcript": transcript})
finally:
    conn.disconnect()
```

Adapt the command and the prompt strings to the target `device_type`. For raw
expect control use `conn.write_channel(...)` + `conn.read_until_pattern(...)`, or
drop to paramiko's `invoke_shell()` channel for fully custom interaction.

## Security note

A network-enabled snippet runs author-written Python with **host network +
credentials** and no network sandbox. Treat it like the `ssh` step's trust
level: only admins create/edit them, keep them minimal, and prefer the `ssh`
step whenever it suffices.
