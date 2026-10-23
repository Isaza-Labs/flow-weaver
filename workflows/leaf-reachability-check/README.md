# leaf-reachability-check

Pings each target device. A reachable device gets a reachability record; an unreachable
one gets a draft incident note. Built from a single sentence in the AI chat.

![Canvas](canvas.png)

| | |
| --- | --- |
| File | [`leaf-reachability-check.bundle.json`](leaf-reachability-check.bundle.json) |
| Format | FlowWeaver workflow bundle, schema `v3` (full export: workflow, snippets and dependency identities) |
| Exported from | FlowWeaver, environment `production` |
| Supported versions | FlowWeaver builds that import bundle schema `v3` |
| Risk level | Low: ICMP only. The two python snippets run offline and only reshape data. |

## Flow

| Step | Type | What it does |
| --- | --- | --- |
| `ping` | `ping` | ICMP ping against the target device |
| `record-reachability` | `python_snippet` `record-reachability` | On ping success: device name, IP, `reachable`, average RTT and a UTC timestamp |
| `open-incident-note` | `python_snippet` `open-incident-note` | On ping failure: a plain-text note tagged `[DRAFT - NOT A TICKET]` with device, IP, detection time and a suggested first check. It never calls a ticketing system. |
| `notify-failure` | `email_send` | Reached when either snippet fails; mails the run owner (`{{ run.owner_email }}`) |

Both snippets fail on purpose when wired to the wrong branch, so a miswired edge cannot go
unnoticed. `config_overrides.ping_node_id` changes the upstream node id (default `ping`),
and `first_check_hint` changes the suggested first check.

## Prerequisites

- Target devices with a management IP, enabled for the environment you run in.
- An SMTP relay for `notify-failure`.

## Inputs

None. The devices are the run targets.

## Outputs

Per device, either the reachability record or the draft incident note, in the run data.

## Import

*Workflows → Import*, pick the bundle, then run **Simulate**. No integration, credential
or address to change.

Run one device per run. When a run has several targets, the engine aggregates the `ping`
status across them: if one device fails, the whole node counts as failed and only the
failure branch runs, for every device.

## QA evidence

Promoted draft → QA → Production with an approver. Two Production runs: a reachable leaf
completed through `record-reachability` (RTT 6.25 ms); an unreachable leaf (documentation
address, 0/4 replies) completed through `open-incident-note`.
