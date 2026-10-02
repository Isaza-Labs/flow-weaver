# Vendor command seed catalog

This directory holds per-vendor `*.yaml` files that seed the
`vendor_commands` table. `VendorCommandValidator` and the agent tools
`validate_ssh_commands` / `find_command` read that table to warn about
unknown or misspelled CLI commands in `ssh` steps. The catalog is
guidance, not authorization: the validator is advisory and never blocks a
command. To block commands, use a policy with an `ssh_command_regex` rule.

## How the files are loaded

On every startup, after the in-code baseline
(`DefaultVendorCommandsSeedService`), `VendorCommandYamlSeedService`
reads `Skills/vendors/*.yaml`:

- It inserts every `(kind, command)` pair missing for that `device_type`.
- On rows that already exist it only fills `description` / `intent` when
  they are empty. It never overwrites a value.
- It does not re-add rows an admin deleted: deletion is a soft delete
  (`IsActive=false`), so the row still exists and is skipped.

**The key is the file's `device_type` field, not the file name.** The only
file that breaks the naming convention is `f5_bigip.yaml`, which contains
`device_type: f5_tmsh`. Files without a `device_type` or without commands
are skipped with a warning.

| File | `device_type` |
|---|---|
| `arista_eos.yaml` | `arista_eos` |
| `cisco_ios.yaml` | `cisco_ios` |
| `cisco_nxos.yaml` | `cisco_nxos` |
| `cisco_xe.yaml` | `cisco_xe` |
| `cisco_xr.yaml` | `cisco_xr` |
| `f5_bigip.yaml` | `f5_tmsh` |
| `fortinet.yaml` | `fortinet` |
| `huawei.yaml` | `huawei` |
| `juniper_junos.yaml` | `juniper_junos` |
| `linux.yaml` | `linux` |
| `nokia_srl.yaml` | `nokia_srl` |
| `nokia_sros.yaml` | `nokia_sros` |
| `paloalto_panos.yaml` | `paloalto_panos` |

## Adding a vendor

1. Drop a YAML file here using the schema below. It is loaded on the next
   startup; no code change is needed.
2. Only if commands for this vendor will be created from the UI or the
   API: add the `device_type` to `VendorFamilyByDeviceType` in
   `Services/VendorCommand/VendorCommandService.cs`. Rows seeded from YAML
   take `vendor_family` from the file.
3. If needed, add the matching parser to
   `deploy/python/flow_weaver_ssh_parsers.py`.

## Schema

```yaml
device_type: cisco_ios
vendor_family: cisco
description: Catalog of known commands for cisco_ios devices.
commands:
  - command: show version
    intent: read
    description: Show software version, uptime, hardware. Idempotent.
  - command: show running-config
    intent: read
    description: Dump running config. Read-only but verbose.
  - command: configure terminal
    intent: write
    description: Enter config mode.
  - command: '^set / system snmp .+$'
    kind: pattern
    intent: write
    description: Any SNMP config line under / system snmp.
```

`kind` is `exact` (default) or `pattern`; a `pattern` entry is a regular
expression matched against the command. `intent` may be `read`, `write`,
or `disruptive`. The validator uses it to escalate the workflow's risk
classification at design time.
