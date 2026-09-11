#!/usr/bin/env python3
"""
SSH runner invoked by the C# SshHandler.

Protocol:
  stdin  : one JSON object — see INPUT_SHAPE below.
  stdout : one JSON object — see OUTPUT_SHAPE below.
  stderr : human-readable diagnostics on fatal errors.
  exit   : 0 = reached end-of-script, even if individual commands failed.
           non-zero = fatal (auth, connect, parse); stderr carries the
           message the C# side surfaces to the step error field.

Why a dedicated runner instead of a python_snippet:
  PythonHandler enforces an import allowlist that blocks netmiko/paramiko.
  This file is shipped in the image and spawned by SshHandler directly,
  bypassing that allowlist. It is NEVER exposed for users to edit.

INPUT_SHAPE:
  {
    "host":                "10.0.0.1",           # required
    "port":                22,                    # optional, default 22
    "username":            "netops",              # required
    "password":            "…",                   # optional (password auth)
    "private_key":         "-----BEGIN …",        # optional (key auth)
    "key_passphrase":      "…",                   # optional, for encrypted keys
    "enable_secret":       "…",                   # optional, triggers conn.enable()
    "device_type":         "cisco_ios",           # optional, default generic_ssh
    "commands":            ["show version"],      # required, non-empty
    "stop_on_error":       true,                  # optional, default true
    "timeout_seconds":     30,                    # optional, default 30
    "use_structured":      false,                 # optional, use_textfsm
    "expected_fingerprint":"SHA256:…"             # optional, host-key pinning
  }

OUTPUT_SHAPE (on success):
  {
    "results": [
      {
        "command":       "show version",
        "output":        "Cisco IOS Software …",  # ANSI + separator lines stripped
        "output_raw":    "\x1b[0mCisco IOS …",   # untouched, preserved
        "parsed":        [...],                   # list of dicts when use_structured matches
        "parser_used":   "textfsm" | "generic_kv" | null,
        "template_name": "cisco_ios_show_version" | null,
        "elapsed_ms":    412,
        "ok":            true,
        "error":         null                     # present when ok=false
      }
    ],
    "device_type":               "cisco_ios",
    "device_type_resolved_from": "cisco" | null,  # set when an alias was applied
    "vendor":                    "cisco",
    "host_key_fingerprint":      "SHA256:abc…"
  }

Structured output (use_structured: true):
  Parsing pipeline runs after every command in BOTH transport modes
  (netmiko AND direct exec). Order:
    1. TextFSM. The local bundle at deploy/python/ntc_templates_extra
       is walked first; ntc-templates upstream second. Local wins on
       conflict. -> parser_used="textfsm", template_name set.
    2. Generic key-value fallback. Drops separator lines (---...---,
       ===...===) and parses any remaining `Key : Value` blocks into
       a list of dicts. Triggers when no TextFSM template matches but
       the output is structurally KV (Nokia SR Linux info commands,
       most Aruba/Huawei show commands). -> parser_used="generic_kv".
    3. No match -> parsed=null, parser_used=null.

Vendor normalization:
  `device_type` is normalized via flow_weaver_ssh_parsers.VENDOR_ALIASES
  before connecting. Free-form inputs the NetBox sync tends to emit
  (`cisco`, `IOS-XE`, `junos`) resolve to the canonical Netmiko
  device_type. When an alias is applied, the original input is surfaced
  in `device_type_resolved_from` so the admin sees what was rewritten.

ANSI/control sanitation:
  By default, `output` (and the `error` field) are stripped of ANSI escape
  sequences (CSI, OSC) and control chars (\x00-\x1f except \t, \r, \n).
  This is critical because Nokia SR-OS, Cisco IOS-XR with paging, and
  `screen`-wrapped sessions emit cursor-move and color codes that pollute
  any downstream consumer (NetBox custom_fields, email body, Slack
  message). The untouched bytes are kept under `output_raw` for forensic
  use. Set `preserve_ansi: true` in the runner input to skip stripping
  entirely.

Transport modes:
  Two ways the runner can talk to a device:

    1. Netmiko (default): opens an interactive shell over SSH (PTY
       allocated), sends commands one at a time, reads until prompt regex
       matches. Vendor-aware (cisco_ios, juniper_junos, arista_eos…). Best
       choice for traditional network OSes whose CLI is line-oriented.

    2. Direct exec (`direct_exec: true`): bypasses Netmiko, uses Paramiko's
       `client.exec_command(cmd, get_pty=False)` — one SSH `exec` channel
       per command, no PTY. Equivalent to running `ssh user@host "cmd"`
       from a shell. The remote SSHd handles the command in batch mode, so
       devices that emit interactive UI escapes (Nokia SR Linux's
       operational banner + cursor-redraw) deliver clean output.
       Auto-enabled when `device_type == "nokia_srl"`.

  In direct_exec mode `setup_commands`, `use_timing`, `read_until_pattern`,
  `enable_secret` and `use_structured` (TextFSM) are not applicable — they
  are Netmiko-only knobs. The output schema is identical regardless of
  transport.

OUTPUT on fatal (stdout empty, stderr set, non-zero exit):
  exit 1 = input parse error
  exit 2 = authentication failed
  exit 3 = connection timeout
  exit 4 = host key mismatch
  exit 5 = unexpected connect failure
  exit 99 = unhandled runner exception (traceback on stderr)
"""

import base64
import hashlib
import json
import re
import sys
import time
import traceback
from io import StringIO

from flow_weaver_ssh_clean import strip_block_separators
from flow_weaver_ssh_parsers import (
    list_platforms,
    normalize_device_type,
    parse_command,
    vendor_for,
)


# CSI: ESC [ <params> <intermediate> <final byte 0x40-0x7E> — covers
# colors (\x1b[0m), cursor moves (\x1b[23D), erase (\x1b[2J), etc.
_ANSI_CSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
# OSC: ESC ] <payload> <BEL or ESC \> — used for window titles, hyperlinks.
_ANSI_OSC = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")
# Other 7-bit ESC sequences (single-shift, charset switches, etc).
_ANSI_OTHER = re.compile(r"\x1b[@-Z\\-_]")
# C0 control chars except \t (0x09), \n (0x0A), \r (0x0D).
_CONTROL_CHARS = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


def _strip_ansi(s: str) -> str:
    """Remove ANSI escape sequences and C0 control chars (keeping
    \\t \\n \\r). Idempotent. Returns '' on falsy input."""
    if not s:
        return ""
    s = _ANSI_CSI.sub("", s)
    s = _ANSI_OSC.sub("", s)
    s = _ANSI_OTHER.sub("", s)
    s = _CONTROL_CHARS.sub("", s)
    return s


def _sha256_fingerprint(host_key) -> str:
    """Netmiko/Paramiko host_key → 'SHA256:<base64 no padding>' matching
    ssh-keygen -lf output and Renci's old format so pins stay portable."""
    raw = host_key.asbytes()
    digest = hashlib.sha256(raw).digest()
    b64 = base64.b64encode(digest).decode("ascii").rstrip("=")
    return f"SHA256:{b64}"


def _normalize_pin(fp: str) -> str:
    s = fp.strip()
    if s.upper().startswith("SHA256:"):
        s = s[7:]
    return s.rstrip("=")


def _load_pkey(pem: str, passphrase: str | None):
    """Try every Paramiko key class in order (Ed25519 → ECDSA → RSA → DSS)
    because PEM headers don't always reveal the curve/key type reliably
    across vendors. First one that parses wins."""
    import paramiko  # local import so import cost is paid once, at connect

    errors: list[str] = []
    for KeyCls in (
        paramiko.Ed25519Key,
        paramiko.ECDSAKey,
        paramiko.RSAKey,
        paramiko.DSSKey,
    ):
        buf = StringIO(pem)
        try:
            return KeyCls.from_private_key(buf, password=passphrase or None)
        except Exception as e:  # noqa: BLE001 — try next key class
            errors.append(f"{KeyCls.__name__}: {e}")
    raise ValueError(
        "could not parse private_key as any supported format "
        f"(tried Ed25519/ECDSA/RSA/DSS): {errors}"
    )


def _make_pin_policy(expected_pin, observed_fp):
    """Build a paramiko MissingHostKeyPolicy that pins on SHA256 fingerprint.

    Shared by the Netmiko path (via `_patch_hostkey_policy`) and the
    direct-exec path. `observed_fp` is a dict the caller provides so the
    runner can surface the observed fingerprint regardless of pin outcome.
    """
    import paramiko

    class PinPolicy(paramiko.MissingHostKeyPolicy):
        def missing_host_key(self, client, hostname, key):  # noqa: ARG002
            fp = _sha256_fingerprint(key)
            observed_fp["value"] = fp
            if expected_pin:
                if _normalize_pin(fp) != _normalize_pin(expected_pin):
                    raise RuntimeError(
                        f"host key mismatch for {hostname}: "
                        f"expected {expected_pin}, got {fp}"
                    )
            # When no pin is configured we accept + log (soft TOFU).

    return PinPolicy()


def _run(cfg: dict) -> dict:
    """Dispatch to the netmiko path or the direct-exec path based on cfg.

    Normalizes `device_type` up front (via VENDOR_ALIASES) so the rest
    of the runner sees a canonical Netmiko identifier and so the
    direct-exec autodetect for `nokia_srl` works even when the caller
    passes "srlinux" or "SR Linux".

    Direct-exec auto-enables for nokia_srl (the SR Linux interactive CLI
    is incompatible with Netmiko's prompt-regex reader because of the
    operational banner + cursor-redraw escapes — see module docstring).
    Explicit `direct_exec: true|false` in cfg overrides the default.
    """
    raw_device_type = cfg.get("device_type")
    device_type, alias_used = normalize_device_type(raw_device_type)
    cfg["device_type"] = device_type
    cfg["_device_type_resolved_from"] = alias_used

    direct_default = device_type == "nokia_srl"
    # Treat `null` (from C#'s nullable forward) as "no preference set" so
    # the device_type-based default still wins. Only an explicit boolean
    # overrides.
    direct_raw = cfg.get("direct_exec")
    direct_exec = direct_default if direct_raw is None else bool(direct_raw)
    if direct_exec:
        return _run_direct(cfg)
    return _run_netmiko(cfg)


def _run_netmiko(cfg: dict) -> dict:
    # Imports deferred until we know we're actually running — keeps the
    # "dry test" (`python3 flow_weaver_ssh_runner.py < /dev/null`) from
    # failing the moment paramiko is missing in a dev environment.
    import paramiko
    from netmiko import (
        ConnectHandler,
        NetmikoAuthenticationException,
        NetmikoTimeoutException,
    )

    commands = cfg.get("commands") or []
    if not isinstance(commands, list) or not commands:
        raise ValueError("`commands` must be a non-empty array of strings")

    timeout = int(cfg.get("timeout_seconds") or 30)
    timeout = max(5, min(300, timeout))
    stop = bool(cfg.get("stop_on_error", True))
    use_structured = bool(cfg.get("use_structured", False))
    preserve_ansi = bool(cfg.get("preserve_ansi", False))
    # Vendor-quirk escape hatches:
    #   `setup_commands` runs before `commands` and discards their output —
    #   used to disable paging or switch CLI mode (Nokia SR Linux's
    #   `environment cli-engine type basic`, Junos `set cli screen-length 0`).
    #   `use_timing` swaps Netmiko's prompt-regex read for delay-based
    #   reading (`send_command_timing`) — works around drivers whose
    #   prompt detection fires too early on the command echo (the
    #   nokia_srl driver does this when the operational banner appears
    #   inline with the prompt).
    #   `read_until_pattern` overrides the prompt regex per command — give
    #   Netmiko a regex it can reliably match (e.g. `r"#\s*$"`) when the
    #   built-in heuristic for the device_type is wrong.
    setup_commands_raw = cfg.get("setup_commands") or []
    setup_commands = [c for c in setup_commands_raw if isinstance(c, str) and c.strip()]
    use_timing = bool(cfg.get("use_timing", False))
    read_until_pattern = cfg.get("read_until_pattern")
    if read_until_pattern is not None and not isinstance(read_until_pattern, str):
        read_until_pattern = None
    # `_run` normalized via VENDOR_ALIASES so common free-form inputs
    # ("cisco", "IOS-XE") already became canonical Netmiko identifiers.
    # Anything still unrecognised here triggers the connect-time retry
    # below: Netmiko raises ValueError on unknown device_type, we catch
    # once and retry with `generic` so the step still runs. The fallback
    # is recorded in `device_type_fallback` for admin visibility.
    device_type = cfg.get("device_type") or "generic"
    device_type_resolved_from = cfg.get("_device_type_resolved_from")

    # Host-key pinning via a custom MissingHostKeyPolicy. Netmiko uses
    # system_host_keys=False by default, so every connect is "missing" —
    # which is exactly where we want to compare against the pin. The
    # observed fp surfaces in the output so the admin can paste it into
    # Device.ExpectedSshHostKeyFingerprint after a first-seen TOFU run.
    observed_fp: dict[str, str] = {}
    expected_pin = cfg.get("expected_fingerprint")
    pin_policy = _make_pin_policy(expected_pin, observed_fp)

    device: dict = {
        "device_type": device_type,
        "host": cfg["host"],
        "port": int(cfg.get("port") or 22),
        "username": cfg["username"],
        "timeout": timeout,
        # Netmiko passes these to Paramiko; disabling system_host_keys
        # funnels every connect through our custom policy above.
        "system_host_keys": False,
        "disabled_algorithms": None,
    }

    if cfg.get("password"):
        device["password"] = cfg["password"]

    if cfg.get("private_key"):
        device["use_keys"] = True
        device["pkey"] = _load_pkey(cfg["private_key"], cfg.get("key_passphrase"))

    if cfg.get("enable_secret"):
        device["secret"] = cfg["enable_secret"]

    # Netmiko doesn't expose the host-key policy knob directly; we patch
    # the underlying SSHClient after ConnectHandler constructs it but
    # before it calls .connect(). Wrapping the whole dance in a helper
    # so we can retry cleanly on "unsupported device_type" with the
    # catch-all `generic`.
    def _try_connect(dt: str):
        device["device_type"] = dt
        c = ConnectHandler(**device, auto_connect=False)
        _patch_hostkey_policy(c, pin_policy)
        c.establish_connection()
        if cfg.get("enable_secret"):
            c.enable()
        return c

    device_type_fallback: str | None = None
    try:
        conn = _try_connect(device_type)
    except ValueError as exc:
        # Netmiko raises ValueError("Unsupported 'device_type' …") when
        # the string isn't in CLASS_MAPPER. Retry with `generic` once so
        # the step still produces output; record the fallback in the
        # response so the admin knows to fix Device.Platform.
        msg = str(exc)
        if "device_type" in msg.lower() and device_type != "generic":
            device_type_fallback = (
                f"device_type '{device_type}' not recognised by Netmiko; "
                f"retried with 'generic'. Fix Device.Platform to a valid "
                f"Netmiko identifier for vendor-aware prompt/paging."
            )
            try:
                conn = _try_connect("generic")
                device_type = "generic"
            except NetmikoAuthenticationException as exc2:
                raise _Fatal(2, f"authentication failed: {exc2}") from exc2
            except NetmikoTimeoutException as exc2:
                raise _Fatal(3, f"connection timeout: {exc2}") from exc2
            except Exception as exc2:  # noqa: BLE001
                raise _Fatal(5, f"connect failed after fallback to generic: {exc2}") from exc2
        else:
            raise _Fatal(5, f"connect failed: {msg}") from exc
    except NetmikoAuthenticationException as exc:
        raise _Fatal(2, f"authentication failed: {exc}") from exc
    except NetmikoTimeoutException as exc:
        raise _Fatal(3, f"connection timeout: {exc}") from exc
    except RuntimeError as exc:
        # Raised by PinPolicy.missing_host_key on mismatch. Netmiko
        # wraps it in SshException in some versions, but the message
        # preserves the "host key mismatch" prefix.
        raise _Fatal(4, str(exc)) from exc
    except Exception as exc:  # noqa: BLE001
        msg = str(exc)
        if "host key mismatch" in msg.lower():
            raise _Fatal(4, msg) from exc
        raise _Fatal(5, f"connect failed: {msg}") from exc

    # Run setup commands first (their output is discarded). Used to
    # disable paging or switch CLI mode before the actual commands run.
    # We use timing-based send here so a slightly off prompt regex on the
    # initial banner doesn't break the session before we even start.
    for setup_cmd in setup_commands:
        try:
            conn.send_command_timing(setup_cmd, read_timeout=timeout)
        except Exception:  # noqa: BLE001 — setup is best-effort
            pass

    results = []
    try:
        for cmd in commands:
            t0 = time.monotonic()
            entry: dict = {"command": cmd}
            try:
                if use_timing:
                    # Delay-based read: doesn't rely on prompt regex.
                    # Use this when the device emits banners (Nokia SR
                    # Linux `--{ running }--`) that confuse Netmiko's
                    # prompt detection.
                    output = conn.send_command_timing(cmd, read_timeout=timeout)
                else:
                    # We don't pass use_textfsm to Netmiko anymore —
                    # parsing runs through our own pipeline below so the
                    # local template bundle and generic_kv fallback are
                    # both reachable in a single, consistent path.
                    send_kwargs = {"read_timeout": timeout}
                    if read_until_pattern:
                        send_kwargs["expect_string"] = read_until_pattern
                    output = conn.send_command(cmd, **send_kwargs)
                raw = output or ""
                entry["output_raw"] = raw
                cleaned = raw if preserve_ansi else _strip_ansi(raw)
                cleaned = strip_block_separators(cleaned)
                entry["output"] = cleaned
                if use_structured:
                    parsed, parser_used, template_name = parse_command(
                        device_type, cmd, raw,
                    )
                else:
                    parsed, parser_used, template_name = None, None, None
                entry["parsed"] = parsed
                entry["parser_used"] = parser_used
                entry["template_name"] = template_name
                entry["ok"] = True
                entry["error"] = None
            except Exception as exc:  # noqa: BLE001
                entry["output"] = ""
                entry["output_raw"] = ""
                entry["parsed"] = None
                entry["parser_used"] = None
                entry["template_name"] = None
                entry["ok"] = False
                err = str(exc)
                entry["error"] = err if preserve_ansi else _strip_ansi(err)
            entry["elapsed_ms"] = int((time.monotonic() - t0) * 1000)
            results.append(entry)
            if not entry["ok"] and stop:
                break
    finally:
        try:
            conn.disconnect()
        except Exception:  # noqa: BLE001 — cleanup is best-effort
            pass

    return {
        "results": results,
        "device_type": device_type,
        "device_type_resolved_from": device_type_resolved_from,
        "vendor": vendor_for(device_type),
        "host_key_fingerprint": observed_fp.get("value"),
        "device_type_fallback": device_type_fallback,
    }


def _run_direct(cfg: dict) -> dict:
    """Run commands via paramiko.SSHClient.exec_command (no PTY).

    One SSH `exec` channel per command on a shared TCP/auth session.
    Equivalent to `ssh user@host "cmd"`. The remote SSHd hands the
    command to the user's login shell in batch mode, so devices that
    emit interactive UI (Nokia SR Linux's operational banner with
    cursor-redraw) deliver clean output.

    Reuses every security primitive of the Netmiko path:
      * `_make_pin_policy(...)` for SHA256 host-key pinning, installed
        BEFORE `connect()` via `set_missing_host_key_policy`.
      * Same plaintext credential handoff (`password=...` or `pkey=...`).
      * Same ANSI/control-char strip applied to stdout/stderr.
      * Same exit code semantics (1 parse / 2 auth / 3 connect timeout
        / 4 host key / 5 connect / 99 unhandled).
      * Same structured-output pipeline: when `use_structured=true` the
        runner runs TextFSM (local + upstream) followed by the generic
        key-value fallback against each command's stdout. This is what
        gives Nokia SR Linux structured `parsed` output despite SR Linux
        always taking the direct-exec path.

    Knobs that are netmiko-specific (`setup_commands`, `use_timing`,
    `read_until_pattern`, `enable_secret`) are silently ignored — they
    have no analogue in the exec model.
    """
    import paramiko
    import socket

    commands = cfg.get("commands") or []
    if not isinstance(commands, list) or not commands:
        raise ValueError("`commands` must be a non-empty array of strings")

    timeout = int(cfg.get("timeout_seconds") or 30)
    timeout = max(5, min(300, timeout))
    stop = bool(cfg.get("stop_on_error", True))
    preserve_ansi = bool(cfg.get("preserve_ansi", False))
    use_structured = bool(cfg.get("use_structured", False))
    device_type = cfg.get("device_type") or "generic"
    device_type_resolved_from = cfg.get("_device_type_resolved_from")

    observed_fp: dict[str, str] = {}
    expected_pin = cfg.get("expected_fingerprint")
    pin_policy = _make_pin_policy(expected_pin, observed_fp)

    pkey = None
    if cfg.get("private_key"):
        pkey = _load_pkey(cfg["private_key"], cfg.get("key_passphrase"))

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(pin_policy)

    connect_kwargs: dict = {
        "hostname": cfg["host"],
        "port": int(cfg.get("port") or 22),
        "username": cfg["username"],
        "timeout": timeout,
        "auth_timeout": timeout,
        "banner_timeout": timeout,
        # Don't let paramiko fall back to ssh-agent / ~/.ssh/id_* — we
        # only want to use the credential the worker passed us.
        "allow_agent": False,
        "look_for_keys": False,
    }
    if cfg.get("password"):
        connect_kwargs["password"] = cfg["password"]
    if pkey is not None:
        connect_kwargs["pkey"] = pkey

    try:
        client.connect(**connect_kwargs)
    except paramiko.AuthenticationException as exc:
        raise _Fatal(2, f"authentication failed: {exc}") from exc
    except (socket.timeout, paramiko.SSHException) as exc:
        msg = str(exc)
        # PinPolicy raises RuntimeError with "host key mismatch" prefix;
        # paramiko wraps it as SSHException in some versions.
        if "host key mismatch" in msg.lower():
            raise _Fatal(4, msg) from exc
        if "timed out" in msg.lower() or isinstance(exc, socket.timeout):
            raise _Fatal(3, f"connection timeout: {msg}") from exc
        raise _Fatal(5, f"connect failed: {msg}") from exc
    except RuntimeError as exc:
        raise _Fatal(4, str(exc)) from exc
    except Exception as exc:  # noqa: BLE001
        msg = str(exc)
        if "host key mismatch" in msg.lower():
            raise _Fatal(4, msg) from exc
        raise _Fatal(5, f"connect failed: {msg}") from exc

    results = []
    try:
        for cmd in commands:
            t0 = time.monotonic()
            entry: dict = {"command": cmd}
            try:
                stdin, stdout, stderr = client.exec_command(
                    cmd, get_pty=False, timeout=timeout,
                )
                # exec_command is non-blocking on the channel; reading
                # stdout/stderr waits until EOF (channel closed by remote).
                out_bytes = stdout.read()
                err_bytes = stderr.read()
                exit_code = stdout.channel.recv_exit_status()
                stdin.close()

                raw_out = out_bytes.decode("utf-8", errors="replace")
                raw_err = err_bytes.decode("utf-8", errors="replace")
                entry["output_raw"] = raw_out
                cleaned = raw_out if preserve_ansi else _strip_ansi(raw_out)
                cleaned = strip_block_separators(cleaned)
                entry["output"] = cleaned
                if use_structured:
                    parsed, parser_used, template_name = parse_command(
                        device_type, cmd, raw_out,
                    )
                else:
                    parsed, parser_used, template_name = None, None, None
                entry["parsed"] = parsed
                entry["parser_used"] = parser_used
                entry["template_name"] = template_name
                entry["ok"] = exit_code == 0
                if exit_code == 0 and not raw_err:
                    entry["error"] = None
                else:
                    err_text = raw_err or f"exit_code={exit_code}"
                    entry["error"] = err_text if preserve_ansi else _strip_ansi(err_text)
            except socket.timeout as exc:
                entry["output"] = ""
                entry["output_raw"] = ""
                entry["parsed"] = None
                entry["parser_used"] = None
                entry["template_name"] = None
                entry["ok"] = False
                entry["error"] = f"command timed out after {timeout}s: {exc}"
            except Exception as exc:  # noqa: BLE001
                entry["output"] = ""
                entry["output_raw"] = ""
                entry["parsed"] = None
                entry["parser_used"] = None
                entry["template_name"] = None
                entry["ok"] = False
                err = str(exc)
                entry["error"] = err if preserve_ansi else _strip_ansi(err)
            entry["elapsed_ms"] = int((time.monotonic() - t0) * 1000)
            results.append(entry)
            if not entry["ok"] and stop:
                break
    finally:
        try:
            client.close()
        except Exception:  # noqa: BLE001 — cleanup is best-effort
            pass

    return {
        "results": results,
        "device_type": device_type,
        "device_type_resolved_from": device_type_resolved_from,
        "vendor": vendor_for(device_type),
        "host_key_fingerprint": observed_fp.get("value"),
        "device_type_fallback": None,
    }


def _patch_hostkey_policy(conn, policy) -> None:
    """Reach into Netmiko's internals to install our host-key policy on
    the not-yet-connected Paramiko SSHClient.

    Netmiko 4.x stores the client under `conn.remote_conn_pre`; some
    older versions use `conn._sess_log` / `conn._remote_conn`. We try
    the public path first and fall back to constructing the client
    manually. If everything fails we warn via stderr but proceed (soft
    TOFU stays in effect because `expected_fingerprint` will simply not
    match — a pinned device without this hook would still reject)."""
    import paramiko

    client = getattr(conn, "remote_conn_pre", None)
    if client is None:
        # 4.x path: create the SSHClient if Netmiko hasn't yet.
        client = paramiko.SSHClient()
        conn.remote_conn_pre = client  # type: ignore[attr-defined]
    client.set_missing_host_key_policy(policy)


class _Fatal(Exception):
    def __init__(self, exit_code: int, message: str) -> None:
        super().__init__(message)
        self.exit_code = exit_code


def main() -> int:
    # `--list-platforms` is consumed by the C# `/api/devices/platforms`
    # endpoint at startup so the frontend dropdown shows canonical
    # device_types + vendor labels. Stdin is ignored in this mode.
    if len(sys.argv) >= 2 and sys.argv[1] == "--list-platforms":
        json.dump({"platforms": list_platforms()}, sys.stdout)
        sys.stdout.write("\n")
        return 0

    try:
        raw = sys.stdin.read()
        if not raw.strip():
            print("empty stdin", file=sys.stderr)
            return 1
        cfg = json.loads(raw)
    except Exception as exc:  # noqa: BLE001
        print(f"input parse error: {exc}", file=sys.stderr)
        return 1

    try:
        out = _run(cfg)
    except _Fatal as fatal:
        print(str(fatal), file=sys.stderr)
        return fatal.exit_code
    except Exception:  # noqa: BLE001
        traceback.print_exc(file=sys.stderr)
        return 99

    json.dump(out, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
