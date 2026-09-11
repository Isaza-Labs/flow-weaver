import json
from pathlib import Path

import pytest

from flow_weaver_ssh_parsers import (
    list_platforms,
    normalize_device_type,
    parse_command,
    vendor_for,
)

FIXTURES = Path(__file__).resolve().parent / "fixtures"


def _load_fixture(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _load_parsed(name: str):
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


# --- normalize_device_type ---------------------------------------------


@pytest.mark.parametrize("raw, expected_dt, expected_alias", [
    ("cisco_ios", "cisco_ios", None),
    ("cisco", "cisco_ios", "cisco"),
    ("IOS-XE", "cisco_xe", "IOS-XE"),
    ("junos", "juniper_junos", "junos"),
    ("Juniper", "juniper_junos", "Juniper"),
    ("eos", "arista_eos", "eos"),
    ("aoscx", "aruba_aoscx", "aoscx"),
    ("SR Linux", "nokia_srl", "SR Linux"),
    ("srlinux", "nokia_srl", "srlinux"),
    ("sros", "nokia_sros", "sros"),
    ("vrp", "huawei", "vrp"),
    ("risecom", "risecom_ros", "risecom"),
    (None, "generic", None),
    ("", "generic", None),
    ("   ", "generic", None),
    # Passthrough — unknown device_type stays as-is so admins can use
    # Netmiko keys we haven't enumerated.
    ("totally_made_up_driver", "totally_made_up_driver", None),
])
def test_normalize_device_type(raw, expected_dt, expected_alias):
    dt, alias = normalize_device_type(raw)
    assert dt == expected_dt
    assert alias == expected_alias


def test_vendor_for_known_and_unknown():
    assert vendor_for("cisco_ios") == "cisco"
    assert vendor_for("nokia_srl") == "nokia"
    assert vendor_for("nokia_sros") == "nokia"
    assert vendor_for("risecom_ros") == "risecom"
    assert vendor_for("totally_made_up_driver") == "generic"


def test_list_platforms_shape():
    platforms = list_platforms()
    assert isinstance(platforms, list)
    assert platforms, "platform list must not be empty"
    sample = next(p for p in platforms if p["value"] == "nokia_srl")
    assert sample["vendor"] == "nokia"
    assert sample["vendor_label"] == "Nokia"
    assert sample["label"] == "Nokia SR Linux"
    # Local bundle ships a Nokia SR Linux template, so the badge fires.
    assert sample["has_local_templates"] is True


def test_list_platforms_marks_unbundled_platforms():
    platforms = list_platforms()
    cisco = next(p for p in platforms if p["value"] == "cisco_ios")
    # No local cisco_ios template in this bundle (yet).
    assert cisco["has_local_templates"] is False


# --- parse_command -----------------------------------------------------


def test_parse_command_nokia_srl_show_version_uses_textfsm():
    raw = _load_fixture("nokia_srl_show_version.txt")
    expected = _load_parsed("nokia_srl_show_version.parsed.json")
    parsed, parser_used, template_name = parse_command(
        "nokia_srl", "show version", raw,
    )
    assert parsed == expected
    assert parser_used == "textfsm"
    assert template_name == "nokia_srl_show_version"


def test_parse_command_nokia_srl_info_alias_resolves_same_template():
    raw = _load_fixture("nokia_srl_show_version.txt")
    parsed, parser_used, template_name = parse_command(
        "nokia_srl", "info from state system information", raw,
    )
    assert parsed is not None
    assert parser_used == "textfsm"
    assert template_name == "nokia_srl_show_version"


def test_parse_command_falls_back_to_generic_kv_when_no_template():
    # Use a fictional command no template covers, against a KV-style
    # output. The generic_kv fallback must produce a list of dicts.
    raw = (
        "Hostname              : node-l3\n"
        "Software Version      : v23.10.2\n"
        "Architecture          : x86_64\n"
    )
    parsed, parser_used, template_name = parse_command(
        "nokia_srl", "info from state system fictional-command", raw,
    )
    assert parsed is not None
    assert parser_used == "generic_kv"
    assert template_name is None
    assert parsed[0]["hostname"] == "node-l3"


def test_parse_command_returns_none_on_empty_input():
    assert parse_command("nokia_srl", "show version", "") == (None, None, None)
    assert parse_command("nokia_srl", "", "stuff") == (None, None, None)


def test_parse_command_returns_none_when_no_parser_matches():
    # A platform we don't have templates for, with output that is not
    # KV-shaped, must return all-None.
    parsed, parser_used, template_name = parse_command(
        "totally_made_up_driver", "show whatever",
        "just a single line of free-form text\n",
    )
    assert parsed is None
    assert parser_used is None
    assert template_name is None
