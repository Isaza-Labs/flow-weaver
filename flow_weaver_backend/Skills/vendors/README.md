# Vendor command seed catalogue

This directory holds per-vendor `*.yaml` files that seed the
`vendor_commands` table on first boot. The platform's
`VendorCommandValidator` reads from that table to gate which CLI commands
are allowed inside `ssh` snippet steps.

## Priority list (S14.3)

The product team has yet to commit to a final vendor priority order.
The following vendors have **scaffold** files in this directory and
require operator review before they are merged into the seed loader:

| Vendor | Status | File |
|---|---|---|
| Cisco IOS / IOS-XE / IOS-XR / NX-OS | Already shipped (in code, not in this dir) | `VendorCommandService` registry |
| Juniper Junos | Already shipped | `VendorCommandService` registry |
| Nokia SR Linux / SR OS | Already shipped | `VendorCommandService` registry |
| Arista EOS | **Scaffold** | `arista_eos.yaml` |
| Fortinet FortiOS | **Scaffold** | `fortinet.yaml` |
| Palo Alto PAN-OS | **Scaffold** | `paloalto_panos.yaml` |
| F5 BIG-IP | **Scaffold** | `f5_bigip.yaml` |

Add a new vendor by:

1. Drop a `<device_type>.yaml` here using the schema below.
2. Wire it into `VendorCommandSeedService` (open follow-up: see
   `docs/vendors.md`).
3. Add the `device_type` to `VendorFamilyByDeviceType` in
   `Services/VendorCommand/VendorCommandService.cs`.
4. Add the matching parser to
   `deploy/python/flow_weaver_ssh_parsers.py`.

## Schema

```yaml
device_type: cisco_ios
vendor_family: cisco
description: Catalogue of allowlisted commands for cisco_ios devices.
commands:
  - command: show version
    intent: read
    description: Show software version, uptime, hardware. Idempotent.
  - command: show running-config
    intent: read
    description: Dump running config. Read-only but verbose.
  - command: configure terminal
    intent: write
    description: Enter config mode. Subsequent commands must be on the
                 explicit allow-list to land.
```

`intent` may be `read`, `write`, or `disruptive`. The validator uses it
to escalate the workflow's risk classification at design time.
