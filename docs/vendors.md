# Vendor catalogue

FlowWeaver's `ssh` snippet handler runs CLI commands against network
devices through Netmiko. Every command goes through
`VendorCommandValidator` before it dispatches: only commands present in
the `vendor_commands` table are allowed to run.

This document describes how the catalogue is organised, how it is
seeded, and how to add a vendor.

## How the catalogue is built

The `vendor_commands` table is filled at startup by two idempotent seed
passes (both wired in `Program.cs`), and can then be edited from
`/vendor-commands` in the UI:

1. **`DefaultVendorCommandsSeedService`** — the in-code baseline
   (`Services/Ai/Seed/DefaultVendorCommandsSeedService.cs`) for
   `cisco_ios`, `cisco_xe`, `cisco_xr`, `cisco_nxos`, `juniper_junos`,
   `arista_eos`, `nokia_sros`, `nokia_srl`, `huawei`, `fortinet` and
   `linux`. It inserts missing rows only; rows an admin deactivated are
   not re-added, and user-authored rows are never touched.
2. **`VendorCommandYamlSeedService`** — runs right after the baseline
   (`Services/Ai/Seed/VendorCommandYamlSeedService.cs`). It walks
   `flow_weaver_backend/Skills/vendors/*.yaml` (copied to the build output
   by the `.csproj`) and, per file, inserts every `(kind, command)` pair not
   already present for that `device_type`. For rows that already exist it
   only backfills an empty `description` / `intent`; it never overwrites
   values an admin or the baseline set.

The vendor family used when a row is created from the UI/API comes from
`VendorFamilyByDeviceType` in
`Services/VendorCommand/VendorCommandService.cs` (unknown device types fall
back to `generic`). Rows created by the YAML seed take `vendor_family` from
the YAML file.

## YAML seed files

| File | `device_type` | Commands |
|---|---|---|
| `arista_eos.yaml` | `arista_eos` | 12 |
| `cisco_ios.yaml` | `cisco_ios` | 95 |
| `cisco_nxos.yaml` | `cisco_nxos` | 76 |
| `cisco_xe.yaml` | `cisco_xe` | 54 |
| `cisco_xr.yaml` | `cisco_xr` | 74 |
| `f5_bigip.yaml` | `f5_tmsh` | 10 |
| `fortinet.yaml` | `fortinet` | 10 |
| `huawei.yaml` | `huawei` | 77 |
| `juniper_junos.yaml` | `juniper_junos` | 105 |
| `linux.yaml` | `linux` | 39 |
| `nokia_srl.yaml` | `nokia_srl` | 98 |
| `nokia_sros.yaml` | `nokia_sros` | 89 |
| `paloalto_panos.yaml` | `paloalto_panos` | 10 |

The Arista, Fortinet, Palo Alto and F5 files are short and cover the most
common read commands. `mikrotik_routeros` and `huawei_vrp` have a vendor
family mapping but no baseline or YAML commands; add them from
`/vendor-commands` or with a new YAML file.

## Adding a vendor

| Step | Where |
|---|---|
| YAML file with `device_type`, `vendor_family` and `commands` | `flow_weaver_backend/Skills/vendors/<device_type>.yaml` (picked up on the next start, no code change) |
| Vendor family for UI/API-created rows | `VendorFamilyByDeviceType` in `VendorCommandService.cs` |
| Output parser, if responses need normalising | `deploy/python/flow_weaver_ssh_parsers.py` |

Each command entry takes `command`, optional `intent` (`read`, `write` or
`disruptive`), optional `description`, and optional `kind` (`exact`, the
default, or `pattern` for a regex entry).

## Authoring guidelines

- Keep the catalogue **small**. It is easier to add a command
  than to remove one once workflows reference it.
- Default to `intent: read`. Promote to `write` or `disruptive` only
  when you are sure the operator wants the elevated risk classification.
- Do not include vendor-specific destructive commands without a
  compensating snippet design (see [`docs/handlers/python_snippet.md`](handlers/python_snippet.md)
  and `Services/Promotion/WorkflowRollbackAnalyzer.cs`).
- Mirror new device_types into the python parser in
  `deploy/python/flow_weaver_ssh_parsers.py` so SSH responses are
  normalised in the same Netmiko session.
