from flow_weaver_ssh_clean import (
    parse_kv_block,
    parse_kv_sections,
    strip_block_separators,
)


def test_strip_block_separators_removes_dashes():
    text = "Header\n--------\nbody\n========\n"
    assert strip_block_separators(text) == "Header\nbody"


def test_strip_block_separators_keeps_decorated_headings():
    # "--[ heading ]--" carries content; only pure separators must go.
    text = "--[ section ]--\n--------\nbody\n"
    assert strip_block_separators(text) == "--[ section ]--\nbody"


def test_strip_block_separators_keeps_blank_lines():
    text = "a\n\nb\n--------\nc\n"
    assert strip_block_separators(text) == "a\n\nb\nc"


def test_strip_block_separators_empty_input():
    assert strip_block_separators("") == ""
    assert strip_block_separators(None) == ""  # type: ignore[arg-type]


def test_parse_kv_block_happy_path():
    text = "Hostname : node-l3\nSoftware Version : v23.10.2\n"
    assert parse_kv_block(text) == {
        "hostname": "node-l3",
        "software_version": "v23.10.2",
    }


def test_parse_kv_block_keeps_colons_in_value():
    text = "Last Booted : 2026-04-27T16:19:01.915Z\nArch : x86_64\n"
    out = parse_kv_block(text)
    assert out is not None
    assert out["last_booted"] == "2026-04-27T16:19:01.915Z"
    assert out["arch"] == "x86_64"


def test_parse_kv_block_returns_none_when_under_two_pairs():
    # Single "key:value" line is not enough — could be prose like
    # "Press any key to continue:".
    assert parse_kv_block("Hostname : x\nrandom prose\n") is None


def test_parse_kv_block_first_occurrence_wins():
    text = "Name : first\nName : second\nOther : x\n"
    out = parse_kv_block(text)
    assert out == {"name": "first", "other": "x"}


def test_parse_kv_block_normalizes_keys():
    text = "System HW MAC Address : aa:bb\nFree Memory (kB) : 100\n"
    out = parse_kv_block(text)
    assert out is not None
    assert "system_hw_mac_address" in out
    # Parenthetical hint dropped from the key.
    assert "free_memory" in out


def test_parse_kv_sections_splits_on_separators():
    text = (
        "ethernet-1/1\n"
        "  admin-state : enable\n"
        "  oper-state  : up\n"
        "----------\n"
        "ethernet-1/2\n"
        "  admin-state : enable\n"
        "  oper-state  : down\n"
    )
    out = parse_kv_sections(text)
    assert out is not None
    assert len(out) == 2
    assert out[0]["oper_state"] == "up"
    assert out[1]["oper_state"] == "down"


def test_parse_kv_sections_returns_none_on_unstructured_text():
    assert parse_kv_sections("Just a banner with no KV pairs.\n") is None
