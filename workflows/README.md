# Workflows

Reusable, inspectable workflow artifacts and examples.

Each workflow should declare its purpose, prerequisites, inputs, outputs, risk level, supported versions and QA evidence. Never publish embedded credentials, real infrastructure identifiers or customer data.

First-party content in this directory is licensed under the root Apache 2.0 license.


## Samples

Each folder holds the full export of one workflow (`<name>.bundle.json`), a capture of its
canvas (`canvas.png`) and a `README.md` describing it.

| Workflow | What it does | Systems | Risk |
| --- | --- | --- | --- |
| [dns-audit-netbox-infoblox](dns-audit-netbox-infoblox/) | Checks active NetBox devices against Infoblox host records, tags them `dns-verified` / `missing-dns` and emails a CSV report | NetBox, Infoblox, email | Medium |
| [netbox-devices-csv-export](netbox-devices-csv-export/) | Exports every NetBox device, following pagination, as a CSV report | NetBox | Low |
| [windows-kb-inventory](windows-kb-inventory/) | Windows patch inventory over SSH with read-only PowerShell; HTML report with the devices requiring attention | Windows (OpenSSH), email | Low |
| [silent-packet-loss-check](silent-packet-loss-check/) | Diagnoses loss on a link whose ports report clean counters, using NetBox cabling and Prometheus telemetry; annotates Grafana and emails the verdict | NetBox, Grafana MCP, Prometheus, email | Low |
| [leaf-reachability-check](leaf-reachability-check/) | Pings each target; records reachable devices and drafts an incident note for unreachable ones | ICMP, email | Low |

### Importing a sample

*Workflows → Import*, pick the `.bundle.json` file. A bundle carries its snippets and the
identities of what it depends on: integrations by slug, MCP servers by name, credentials by
reference. It never carries secrets or auth configuration, and an integration's base URL is
only a hint (`*.example.com` in these samples). The workflow lands as a draft; each README
lists what to point at your instance before the first **Simulate** and run.

Email addresses in the samples are `netops@example.com`; replace them after import.
