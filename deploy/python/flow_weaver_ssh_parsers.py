"""
Vendor normalization and structured-output parsing for the SSH runner.

Two responsibilities:
  1. Map free-form vendor strings (NetBox sync emits "cisco", users type
     "IOS-XE", "junos", etc.) to canonical Netmiko `device_type` values.
     This avoids the silent fallback to `generic` that would otherwise
     drop TextFSM support whenever the platform string is anything but
     the exact Netmiko key.
  2. Parse raw command output through a chain:
       a) TextFSM. Two index files are walked in priority order — first
          the local bundle at `deploy/python/ntc_templates_extra`, then
          ntc-templates upstream. The first matching template runs via
          `textfsm` directly (NOT via `ntc_templates.parse.parse_output`,
          which only honours a SINGLE NET_TEXTFSM directory and would
          make one tier unreachable). Local bundle wins on conflict.
       b) Generic key-value fallback (flow_weaver_ssh_clean) when no
          TextFSM template matches — covers Nokia SR Linux `info from
          state ...`, Risecom show commands, ad-hoc show outputs.
     The first parser that produces a non-empty result wins; metadata
     (`parser_used`, `template_name`) is returned so the frontend can
     show "structured by textfsm: nokia_srl_show_version" or
     "structured by generic key-value parser".
"""

import re
from pathlib import Path
from typing import Iterator

from flow_weaver_ssh_clean import parse_kv_sections, strip_block_separators


_LOCAL_TEMPLATES_DIR = Path(__file__).resolve().parent / "ntc_templates_extra"


# Canonical Netmiko device_type -> coarse vendor family. The vendor
# family is what the frontend groups platforms under; the OS variants
# (cisco_ios / cisco_xe / cisco_xr) all roll up to "cisco".
VENDOR_FAMILY: dict[str, str] = {
    "cisco_ios": "cisco",
    "cisco_xe": "cisco",
    "cisco_xr": "cisco",
    "cisco_nxos": "cisco",
    "cisco_asa": "cisco",
    "juniper_junos": "juniper",
    "juniper": "juniper",
    "arista_eos": "arista",
    "aruba_aoscx": "aruba",
    "aruba_osswitch": "aruba",
    "aruba_os": "aruba",
    "hp_procurve": "aruba",
    "hp_comware": "hpe",
    "huawei": "huawei",
    "huawei_vrp": "huawei",
    "huawei_vrpv8": "huawei",
    "nokia_sros": "nokia",
    "nokia_srl": "nokia",
    "fortinet": "fortinet",
    "mikrotik_routeros": "mikrotik",
    "linux": "linux",
    "risecom_ros": "risecom",
    "generic": "generic",
}


# Free-form input (lowercased) -> canonical Netmiko device_type.
# Anything not matching here passes through unchanged so admins can use
# Netmiko device_types we haven't enumerated.
VENDOR_ALIASES: dict[str, str] = {
    # Cisco
    "cisco": "cisco_ios",
    "ios": "cisco_ios",
    "ios-xe": "cisco_xe",
    "iosxe": "cisco_xe",
    "ios xe": "cisco_xe",
    "ios-xr": "cisco_xr",
    "iosxr": "cisco_xr",
    "ios xr": "cisco_xr",
    "nxos": "cisco_nxos",
    "nx-os": "cisco_nxos",
    "nx os": "cisco_nxos",
    "asa": "cisco_asa",
    # Juniper
    "junos": "juniper_junos",
    "juniper": "juniper_junos",
    # Arista
    "eos": "arista_eos",
    "arista": "arista_eos",
    # Aruba / HPE
    "aruba": "aruba_aoscx",
    "aoscx": "aruba_aoscx",
    "aos-cx": "aruba_aoscx",
    "aos_cx": "aruba_aoscx",
    "procurve": "aruba_osswitch",
    "aos-switch": "aruba_osswitch",
    "comware": "hp_comware",
    # Huawei
    "vrp": "huawei",
    # Nokia
    "sros": "nokia_sros",
    "sr os": "nokia_sros",
    "sr-os": "nokia_sros",
    "timos": "nokia_sros",
    "srlinux": "nokia_srl",
    "sr linux": "nokia_srl",
    "sr-linux": "nokia_srl",
    "srl": "nokia_srl",
    # Fortinet
    "fortigate": "fortinet",
    "fortios": "fortinet",
    # MikroTik
    "mikrotik": "mikrotik_routeros",
    "routeros": "mikrotik_routeros",
    # Risecom (no upstream Netmiko driver — connects via `generic` but
    # the alias is preserved so the parser dispatch can find local
    # templates under `risecom_ros_*.textfsm`).
    "risecom": "risecom_ros",
    "ros": "risecom_ros",
}


# Vendor family -> human label for the platform picker dropdown.
VENDOR_LABELS: dict[str, str] = {
    "cisco": "Cisco",
    "juniper": "Juniper",
    "arista": "Arista",
    "aruba": "Aruba / HPE",
    "huawei": "Huawei",
    "nokia": "Nokia",
    "fortinet": "Fortinet",
    "mikrotik": "MikroTik",
    "risecom": "Risecom",
    "linux": "Linux",
    "hpe": "HPE Comware",
    "generic": "Generic / unknown",
}


# Display labels for device_types. Falls back to the device_type
# itself when not listed.
DEVICE_TYPE_LABELS: dict[str, str] = {
    "cisco_ios": "Cisco IOS",
    "cisco_xe": "Cisco IOS-XE",
    "cisco_xr": "Cisco IOS-XR",
    "cisco_nxos": "Cisco NX-OS",
    "cisco_asa": "Cisco ASA",
    "juniper_junos": "Juniper Junos",
    "arista_eos": "Arista EOS",
    "aruba_aoscx": "Aruba AOS-CX",
    "aruba_osswitch": "Aruba AOS-Switch (Procurve)",
    "huawei": "Huawei VRP",
    "nokia_sros": "Nokia SR OS",
    "nokia_srl": "Nokia SR Linux",
    "fortinet": "Fortinet FortiGate",
    "mikrotik_routeros": "MikroTik RouterOS",
    "risecom_ros": "Risecom ROS",
    "linux": "Linux shell",
    "generic": "Generic SSH",
}


def normalize_device_type(raw: str | None) -> tuple[str, str | None]:
    """Map a user/NetBox-supplied vendor string to a Netmiko device_type.

    Returns `(device_type, alias_used)`. `alias_used` is the original
    raw input ONLY when an alias mapping kicked in — useful so the
    runner can surface "you typed 'cisco', we used 'cisco_ios'".

    A passthrough (raw already a Netmiko device_type) returns
    `(raw_lowered, None)`. An empty/None raw returns `("generic", None)`.
    """
    if not raw:
        return "generic", None
    key = raw.strip().lower()
    if not key:
        return "generic", None
    if key in VENDOR_ALIASES:
        mapped = VENDOR_ALIASES[key]
        return (mapped, raw) if mapped != key else (mapped, None)
    return key, None


def vendor_for(device_type: str) -> str:
    """Return the coarse vendor family for a Netmiko device_type."""
    return VENDOR_FAMILY.get(device_type, "generic")


def list_platforms() -> list[dict]:
    """Enumerate canonical platforms for the frontend picker. Output
    shape per entry:
        {
          "value":               "cisco_ios",
          "label":               "Cisco IOS",
          "vendor":              "cisco",
          "vendor_label":        "Cisco",
          "has_local_templates": false
        }
    """
    out = []
    for dt, vendor in sorted(VENDOR_FAMILY.items()):
        out.append({
            "value": dt,
            "label": DEVICE_TYPE_LABELS.get(dt, dt),
            "vendor": vendor,
            "vendor_label": VENDOR_LABELS.get(vendor, vendor),
            "has_local_templates": _has_local_template(dt),
        })
    return out


def _has_local_template(device_type: str) -> bool:
    """True if the local bundle ships at least one template for this
    device_type. Used to badge platforms with "structured output ready"
    in the frontend picker."""
    if not _LOCAL_TEMPLATES_DIR.is_dir():
        return False
    return any(_LOCAL_TEMPLATES_DIR.glob(f"{device_type}_*.textfsm"))


def parse_command(
    device_type: str,
    command: str,
    raw_output: str,
) -> tuple[list | None, str | None, str | None]:
    """Run the parser chain on a single command's raw output. Returns
    `(parsed, parser_used, template_name)`.

    Pipeline:
      1. TextFSM via ntc-templates. The local bundle ships first on
         NET_TEXTFSM so vendor-specific fixes win over upstream; the
         upstream package fills in the long tail. On match, returns
         `parser_used="textfsm"` and the resolved template basename.
      2. Generic key-value fallback (`parse_kv_sections` after stripping
         separators). On match, returns `parser_used="generic_kv"` and
         `template_name=None`.
      3. No match -> `(None, None, None)`.

    `parsed` is always a list of dicts (TextFSM and `parse_kv_sections`
    both return a list) so consumers don't have to branch on shape.
    """
    if not raw_output or not command:
        return None, None, None

    parsed, template_name = _try_textfsm(device_type, command, raw_output)
    if parsed:
        return parsed, "textfsm", template_name

    sections = parse_kv_sections(strip_block_separators(raw_output))
    if sections:
        return sections, "generic_kv", None

    return None, None, None


def _try_textfsm(
    device_type: str,
    command: str,
    raw_output: str,
) -> tuple[list | None, str | None]:
    """Walk index files (local first, upstream second) and run TextFSM
    directly on the first matching template. Returns `(parsed_list,
    template_name)` on a hit, `(None, None)` on miss.

    We bypass `ntc_templates.parse.parse_output` because it only honours
    a single template directory; running TextFSM ourselves lets the
    local bundle override upstream cleanly while keeping upstream
    reachable for the long tail."""
    cmd_norm = command.strip()
    for templates_dir, index_path in _index_paths():
        for template_file, _, platform, command_re in _read_index(index_path):
            if platform != device_type:
                continue
            try:
                if not re.search(_to_textfsm_cmd_regex(command_re), cmd_norm):
                    continue
            except re.error:
                continue
            template_path = templates_dir / template_file
            if not template_path.is_file():
                continue
            parsed = _run_textfsm(template_path, raw_output)
            if parsed:
                return parsed, template_file.removesuffix(".textfsm")
    return None, None


def _index_paths() -> Iterator[tuple[Path, Path]]:
    """Yield `(templates_dir, index_path)` pairs in priority order:
    local bundle first, ntc-templates upstream second. Skips tiers
    that aren't present (no local bundle, ntc_templates not installed)."""
    if _LOCAL_TEMPLATES_DIR.is_dir():
        idx = _LOCAL_TEMPLATES_DIR / "index"
        if idx.is_file():
            yield _LOCAL_TEMPLATES_DIR, idx
    try:
        import ntc_templates
    except ImportError:
        return
    upstream_dir = Path(ntc_templates.__file__).resolve().parent / "templates"
    upstream_idx = upstream_dir / "index"
    if upstream_idx.is_file():
        yield upstream_dir, upstream_idx


def _read_index(path: Path) -> Iterator[tuple[str, str, str, str]]:
    """Yield `(template_file, hostname_re, platform, command_re)` rows
    from an ntc-templates index file. Skips comments, blank lines, and
    the optional `Template, Hostname, Platform, Command` header row."""
    try:
        with path.open(encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if line.lower().startswith("template,"):
                    continue
                parts = [p.strip() for p in line.split(",")]
                if len(parts) < 4:
                    continue
                yield parts[0], parts[1], parts[2], parts[3]
    except OSError:
        return


def _run_textfsm(template_path: Path, raw_output: str) -> list[dict] | None:
    """Compile and run a TextFSM template against `raw_output`. Returns
    a list of dicts (one per record) on success, None on parse error or
    no records matched."""
    try:
        import textfsm
    except ImportError:
        return None
    try:
        with template_path.open(encoding="utf-8") as fh:
            fsm = textfsm.TextFSM(fh)
        rows = fsm.ParseText(raw_output)
    except Exception:  # noqa: BLE001 — malformed template, log nothing
        return None
    if not rows:
        return None
    keys = [h.lower() for h in fsm.header]
    return [dict(zip(keys, row)) for row in rows]


def _to_textfsm_cmd_regex(pattern: str) -> str:
    """ntc-templates index uses a shorthand where `sh[[ow]]` means
    `sh(ow)?` — bracketed groups are optional suffixes. Convert to a
    plain regex anchored at the start so we can `re.search` it against
    the user's command string."""
    out = []
    i = 0
    while i < len(pattern):
        if pattern[i:i + 2] == "[[":
            end = pattern.find("]]", i + 2)
            if end == -1:
                out.append(pattern[i])
                i += 1
                continue
            inside = pattern[i + 2:end]
            out.append(f"(?:{inside})?")
            i = end + 2
        else:
            out.append(pattern[i])
            i += 1
    return "^" + "".join(out)
