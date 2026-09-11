# Local TextFSM template bundle

This directory ships TextFSM templates that override or extend
ntc-templates upstream. The SSH runner walks `index` here before the
upstream index, so a template added here wins on conflict.

## Layout

```
ntc_templates_extra/
├── index                              # ntc-templates index format
├── <platform>_<command>.textfsm        # one template per (platform, command)
└── README.md                           # this file
```

Index format (4 columns, comma-separated):

```
Template, Hostname, Platform, Command
nokia_srl_show_version.textfsm, .*, nokia_srl, sh[[ow]] ver[[sion]]
```

`sh[[ow]]` is the index shorthand for `sh(ow)?` — bracketed groups are
optional command suffixes. Multiple lines may point at the same template
file when one parser covers several command aliases (e.g. SR Linux's
`show version` is a bash alias for `info from state system information`).

## Adding a template

1. Capture a representative output from a real device:
   `tests/python/fixtures/<platform>_<command_underscored>.txt`.
2. Author `<platform>_<command>.textfsm`. Keep TextFSM `Value` names in
   UPPERCASE (the runner lowercases them in the parsed dict, matching
   upstream ntc-templates convention).
3. Append an entry to `index`.
4. Add a parsed-expectation fixture
   `tests/python/fixtures/<platform>_<command_underscored>.parsed.json`
   and assert `parse_command(...)` returns it.

## Why bypass ntc-templates' own loader

`ntc_templates.parse.parse_output` honours a single template directory
through the `NET_TEXTFSM` env var. Using it would force a choice between
"only local" or "only upstream". The runner walks indexes itself
(`flow_weaver_ssh_parsers._index_paths`) so both tiers stay reachable
and override semantics are explicit.

## Vendors covered here

| Platform | Why local | Status |
|---|---|---|
| `nokia_srl` | No upstream coverage; SR Linux info-style output. | `show version` |
| `risecom_ros` | No upstream Netmiko driver. | TODO — needs real samples |
| `aruba_aoscx` | Fill gaps in upstream (`show vsx`, etc.). | TODO |
