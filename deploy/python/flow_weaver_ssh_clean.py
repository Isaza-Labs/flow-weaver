"""
Output cleaning and generic key-value parsing for the SSH runner.

Two responsibilities:
  1. Drop pure-separator lines (`---...---`, `===...===`) from device
     output. CLIs like Nokia SR Linux's `info from state ...` and
     several Aruba show commands frame their content with horizontal
     rules that are visual noise once the output reaches a Jinja
     template or a NetBox custom_field.
  2. Provide a generic `key : value` parser as a last-resort fallback
     when no TextFSM template matches. Many devices (every Nokia SR
     Linux info command, several Aruba/Huawei show commands) emit
     stable `Key : Value` blocks — parsing them generically gives
     workflows structured access without per-command template work.

Both helpers are conservative on purpose: `parse_kv_block` refuses to
return a dict unless it sees at least two valid pairs, so prose like
"Press any key to continue:" doesn't get mistaken for structured data.
"""

import re


# Pure-separator line: 8+ chars of -, =, _, +. Whole line (after strip)
# must be separator chars — this avoids eating decorated headings such
# as `--[ heading ]--` that carry information.
_SEPARATOR_LINE = re.compile(r"^\s*[-=_+]{8,}\s*$")

# Key-value line. The key:
#   - starts with a letter (filters IPv4-like values being mistaken for
#     keys when wrapped weirdly),
#   - allows letters/digits/space/_-/().,
#   - is bounded to 60 chars to avoid greedy matching across long lines.
# The first `:` separates key from value; later colons (timestamps like
# 2026-04-27T16:19:01.915Z) stay in the value.
_KV_LINE = re.compile(
    r"^\s*([A-Za-z][A-Za-z0-9 _\-/().,]{0,60}?)\s*:\s*(.+?)\s*$"
)


def strip_block_separators(text: str) -> str:
    """Remove pure-separator lines from `text`. Idempotent. Preserves
    blank lines and content lines verbatim. Returns '' on falsy input."""
    if not text:
        return ""
    return "\n".join(
        line for line in text.splitlines()
        if not _SEPARATOR_LINE.match(line)
    )


def _normalize_key(key: str) -> str:
    """`System HW MAC Address` -> `system_hw_mac_address`. Lowercase,
    drop parenthetical hints, collapse non-alphanumerics to `_`, trim
    leading/trailing underscores."""
    k = key.strip().lower()
    k = re.sub(r"\(.*?\)", "", k)
    k = re.sub(r"[^a-z0-9]+", "_", k)
    return k.strip("_")


def parse_kv_block(text: str) -> dict | None:
    """Parse one block of `key : value` lines into a dict. Returns
    None unless at least two lines match the KV pattern.

    Keys are normalized via `_normalize_key`. Values keep their original
    case and inner whitespace. On duplicate keys, the first occurrence
    wins (matches what users intuitively read top-down)."""
    if not text:
        return None
    out: dict[str, str] = {}
    for line in text.splitlines():
        m = _KV_LINE.match(line)
        if not m:
            continue
        key = _normalize_key(m.group(1))
        if not key or key in out:
            continue
        out[key] = m.group(2)
    return out if len(out) >= 2 else None


def parse_kv_sections(text: str) -> list[dict] | None:
    """Split `text` into sections (separator lines or 2+ blank-line
    groups act as boundaries) and parse each section as a KV block.

    Returns the list of dicts that parsed successfully, or None when
    no section produced a valid dict.

    Multi-section inputs (Nokia SR Linux `info from state interface *`)
    yield one dict per interface; single-section inputs yield a 1-item
    list so consumers always see the same shape."""
    if not text:
        return None
    sections: list[list[str]] = [[]]
    blank_streak = 0
    for line in text.splitlines():
        if _SEPARATOR_LINE.match(line):
            if sections[-1]:
                sections.append([])
            blank_streak = 0
            continue
        if not line.strip():
            blank_streak += 1
            if blank_streak >= 2 and sections[-1]:
                sections.append([])
            continue
        blank_streak = 0
        sections[-1].append(line)

    parsed_sections: list[dict] = []
    for sec in sections:
        if not sec:
            continue
        d = parse_kv_block("\n".join(sec))
        if d is not None:
            parsed_sections.append(d)
    return parsed_sections or None
