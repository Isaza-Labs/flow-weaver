# Vendor catalogue

FlowWeaver's `ssh` snippet handler runs CLI commands against network
devices through Netmiko. Every command goes through
`VendorCommandValidator` before it dispatches: only commands present in
the `vendor_commands` table are allowed to run.

This document describes how the catalogue is organised, the priority
order the team has agreed for new additions, and the open follow-up
needed to wire the new YAML scaffolds into the seed loader.

## Today

In-code catalogue lives in `Services/VendorCommand/VendorCommandService.cs`
under `VendorFamilyByDeviceType`. Local overrides + additions live in
the `vendor_commands` table, edited from `/vendor-commands` in the UI.

Seed catalogue lives under `Skills/vendors/*.yaml`. Currently four
priority vendors have **scaffold** YAML files (Arista, Fortinet,
Palo Alto, F5). They are not yet imported on first boot — see
"Open follow-up" below.

## Priority order (S14.3 product decision)

The product team is finalising the priority order. Working list:

1. **Arista EOS** — seed YAML in place; production-ready content.
2. **Fortinet FortiOS** — seed YAML in place; production-ready content.
3. **Palo Alto PAN-OS** — seed YAML in place; production-ready content.
4. **F5 BIG-IP** (tmsh) — seed YAML in place; covers the most common
   read commands.
5. (next) **Mikrotik RouterOS** — covered by `mikrotik_routeros` in
   the in-code map; needs YAML.
6. (next) **Huawei VRP** — covered by `huawei_vrp` in the in-code map;
   needs YAML.

Each addition needs:

| Step | Status |
|---|---|
| YAML scaffold in `Skills/vendors/<device_type>.yaml` | done for 1–4 |
| Entry in `VendorFamilyByDeviceType` | done for 1–4 |
| Parser in `deploy/python/flow_weaver_ssh_parsers.py` | depends |
| Promotion via `VendorCommandSeedService` (open follow-up) | not started |

## Open follow-up

`VendorCommandSeedService` does not exist yet. To activate the YAML
seed flow, build a service that:

1. Runs after `DevSeedService` and `CatalogSeedService`.
2. Walks `Skills/vendors/*.yaml` and, for every device_type with no rows
   in `vendor_commands` yet, inserts one row per command.
3. Logs a one-line summary per file.

Skeleton:

```csharp
public static class VendorCommandSeedService
{
    public static async Task SeedAsync(
        IServiceScopeFactory scopeFactory, ILogger logger,
        CancellationToken ct = default)
    {
        // Walk Skills/vendors/*.yaml; deserialize into a record;
        // insert one row per command when there are no rows for that
        // device_type yet. Use the existing
        // VendorCommandService.CreateAsync to keep audit + cache
        // invalidation in one place.
    }
}
```

Wire into `Program.cs` next to `CatalogSeedService.SeedAsync`. Add a
test under `flow_weaver_backend.Tests/VendorCommandSeedTests.cs`
asserting the expected row count after a seed pass.

Track the work as a follow-up to S14.3.

## Authoring guidelines

- Keep the catalogue **small** at first. It is easier to add a command
  than to remove one once workflows reference it.
- Default to `intent: read`. Promote to `write` or `disruptive` only
  when you are sure the operator wants the elevated risk classification.
- Do not include vendor-specific destructive commands without a
  compensating snippet design (see [`docs/handlers/python_snippet.md`](handlers/python_snippet.md)
  and `Services/Promotion/WorkflowRollbackAnalyzer.cs`).
- Mirror new device_types into the python parser in
  `deploy/python/flow_weaver_ssh_parsers.py` so SSH responses are
  normalised in the same Netmiko session.
