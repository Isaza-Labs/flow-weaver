# Skill: workflows

Workflows are a **DAG** of nodes connected by edges, targeted at devices or
pools, living in environment `draft`, `qa`, or `production`.

## DAG shape — schema is strict

The backend validates `nodes` / `edges` with `additionalProperties: false`.
Any extra field (`name`, `label`, `service_type`, `display_name`) → fails
with `schema_invalid`.

### Node

```json
{
  "id": "node-1",
  "snippet_id": "<uuid> | __start__ | __end__ | subflow",
  "x": 0, "y": 0,
  "type": "task",
  "config_overrides": {}
}
```

- `snippet_id` — real Snippet UUID, OR one of three sentinels
  (`__start__`, `__end__`, `subflow`). **Type names like
  `"integration_action"`, `"ping"`, `"report"` are NOT sentinels** —
  resolve their UUID with `fw_snippets:list_snippets(type=...)`.
- `x` / `y` — always `0`. The editor's Dagre auto-layout re-places nodes
  on load; manual coordinates only fight it.
- `type` — `task` (default), `decision`, `subflow`. Omit for normal nodes.
- `config_overrides` — merged into snippet defaults at run time. Shape
  follows the snippet's `input_schema`.

No other properties. `name`, `label`, `service_type` belong on the
**Snippet**, not the node.

### Edge

```json
{ "source": "node-1", "target": "node-2", "type": "success", "condition": "<only when type=conditional>" }
```

Flow Weaver supports **four** edge types — pick the one that matches
the routing intent, not the user's wording. All four are first-class.

| Route based on… | `type` | `condition` |
|---|---|---|
| Source step ran without raising | `success` | n/a |
| Source step raised | `failure` | n/a |
| Both branches must fire (cleanup, notify-always) | `always` | n/a |
| A specific VALUE the step emitted (multi-way value branching) | `conditional` | **required**, non-empty |

### When to use `conditional`

Use it whenever the routing decision depends on the **content** of a
previous step's output, not on whether the step succeeded. Engine
support is real and tested — `ConditionEvaluator` resolves
`{{ steps.X.output.path }}` templates and evaluates the expression with
`==`, `!=`, `>=`, `<=`, `>`, `<`, `&&`, `||`. Missing operator → truthy
check (true / non-zero / non-empty string).

Concrete examples that ARE `conditional`:

```jsonc
// Branch by HTTP status class
{ "source": "rest-1", "target": "ok-path",
  "type": "conditional",
  "condition": "{{ steps.rest-1.output.status_code }} >= 200 && {{ steps.rest-1.output.status_code }} < 300" }

// Branch by vendor in classify-step output
{ "source": "classify", "target": "cisco-handler",
  "type": "conditional",
  "condition": "{{ steps.classify.output.vendor }} == 'cisco_ios'" }

// Branch by an SSH command's exit_code on a specific device index
{ "source": "ssh-1", "target": "remediate",
  "type": "conditional",
  "condition": "{{ steps.ssh-1.output.devices[0].output.exit_code }} != 0" }
```

### What a failure does to the rest of the run

A run **stops at the first failed node whose failure no `failure` edge consumes**. This is on
by default (`stop_on_failure`), and it is the single most important thing to get right when
you wire error handling:

| Out of the failed node | Does the walk continue? |
|---|---|
| a `failure` edge | **yes** — the graph says what to do, so it is handled |
| an `always` edge | **no** — `always` means "fire either way", not "here is what to do when this fails" |
| a `success` edge | no, and its target was never going to run anyway |
| nothing | no |

So **if you want something to happen after a step that might fail, wire it behind a `failure`
edge**, not an `always` edge. An `always` edge out of a node that fails does not fire, and its
target is recorded `skipped`.

A node with no incoming edges is not exempt: once the walk has stopped, nothing further is
dispatched.

Before 2026-08-29 none of this was true — a failure stopped only what sat behind a `success`
edge, and everything else carried on. A workflow relying on that keeps working only if you
pass `"stop_on_failure": false` when starting the run, which is something a caller now has to
ask for.

A `conditional` edge only fires out of a node that SUCCEEDED. It is a
`success` edge with a question attached, so a failed source answers it before
the expression is ever read. (Until 2026-08-29 the expression was evaluated
regardless, so `1 == 1` on an edge out of a crashed step fired it — and the
`failure` edge beside it fired too, meaning a run could take both the
compensation branch and the happy branch out of the same failed node.)

If `condition` is missing or empty on a `conditional` edge, the runtime
fails with `conditional edge A->B requires a non-empty condition` —
`simulate_workflow_run` does NOT catch this, so the agent is responsible
for filling it.

### When NOT to use `conditional` (common confusion)

When the user says *"branch based on whether the step succeeded or
failed"* / *"if it works do X, otherwise do Y"* / *"conditional success
and failure paths"*, the right answer is **two edges of type `success`
and `failure`**, not a `conditional` edge with an `exit_code` check.
The engine already routes on the source step's outcome — re-implementing
that with a `conditional` is redundant and brittle.

Decision rule:
- Outcome (succeeded vs raised) → `success` + `failure` (or `always`).
- Output value (status code, vendor name, count, flag) → `conditional` + expression.

The legacy `fallback` label is a synonym for `failure`; the backend
rejects `fallback` as a JSON value, so always emit `failure`.

### Edges — at most one per `(source, target, outcome)` triple

Outcomes covered by edges from the same `(source, target)` pair must not
overlap:

| Wrong | Correct |
|---|---|
| `(A→B, success)` + `(A→B, failure)` | `(A→B, always)` |
| `(A→B, success)` + `(A→B, always)` | `(A→B, always)` |
| `(A→B, failure)` + `(A→B, always)` | `(A→B, always)` |

Two parallel edges between the same nodes don't add safety — they fire
the consumer twice and hide intent. Most "either-way" routings collapse
to one `always`. If you genuinely need to distinguish the path, use two
intermediate nodes, not two edges.

### Sentinel IDs are FIXED

`id` MUST equal `snippet_id` for sentinels:

```json
{ "id": "__start__", "snippet_id": "__start__", "x": 0, "y": 0 }
{ "id": "__end__",   "snippet_id": "__end__",   "x": 0, "y": 0 }
```

Other ids (`"start"`, `"begin"`) make the editor inject synthetic
sentinels on top, leaving the saved DAG visually broken.

### Minimal valid body

```json
{
  "name": "ping-Test",
  "description": "Ping the Test device",
  "input_schema": { "type": "object" },
  "nodes": [
    { "id": "__start__", "snippet_id": "__start__", "x": 0, "y": 0 },
    { "id": "ping-1",    "snippet_id": "<ping-uuid>", "x": 0, "y": 0 },
    { "id": "__end__",   "snippet_id": "__end__",   "x": 0, "y": 0 }
  ],
  "edges": [
    { "source": "__start__", "target": "ping-1",  "type": "success" },
    { "source": "ping-1",    "target": "__end__", "type": "success" }
  ]
}
```

`create_workflow` ignores any `environment` field — every new workflow
lands in `draft`. Use `promote_workflow` after validating.

Don't set `source_handle` / `target_handle` on edges — Dagre picks them
from the final layout.

## Snippet prerequisite

Every `task` node needs a Snippet. Before `create_workflow`:

- Reuse via `fw_snippets:list_snippets` if a matching `(type, target_mode)`
  exists.
- Otherwise create it first with `fw_snippets:create_snippet`. Bundle
  both creates in the **same ONE-PLAN block**.

## `integration_action` nodes — real GUIDs only

Every `integration_action` node needs `config_overrides` with
**top-level** (not nested in `input`):

- `integration_id` — GUID for an active Integration.
- `action_id` — GUID for an active IntegrationAction owned by it.

Names (`"netbox-list-devices"`) and placeholder GUIDs
(`00000000-…-001`, sequentials, all-1s) are rejected with
`references_invalid`. **There is no fallback value**: stop and resolve.

> **Not every external system is a REST integration.** If the user names a system
> (Grafana, a search API, …) and no Integration matches, check `list_mcp_servers`
> before concluding it isn't available — a registered MCP server may already
> expose it, reachable via an `mcp_call` node (see below).

Resolve in the same ONE-PLAN as the create:

1. `fw_integrations:list_integrations` → pick `integration_id`.
2. `fw_integrations:list_palette_actions(integration_id=...)` → pick
   `integration_action_id`.
3. Plug into `config_overrides`:

```json
{
  "id": "fetch-netbox-devices",
  "snippet_id": "<integration_action snippet uuid>",
  "x": 0, "y": 0,
  "config_overrides": {
    "integration_id": "e071207d-2625-475d-8379-f1500f3f62e0",
    "action_id":      "7b0c1a9d-0e4a-4f00-9c3f-1b8f2a0c4d11",
    "query": { "status": "active", "limit": 1000 }
  }
}
```

## `mcp_call` nodes — reach a registered MCP server

An **MCP server** an admin registered (Govern → MCP servers) is a *second* way to
reach an external system, alongside `integration_action`. When the user names a
system and no Integration matches, **check `list_mcp_servers` before concluding it
isn't available** — a server with `status: ok` is often the answer, and its tools
already carry input schemas.

Reuse the seeded `mcp_call` snippet (`fw_snippets:list_snippets(type="mcp_call")`,
`target_mode=once`). The node needs `config_overrides` with **top-level** (not
nested in `input`):

- `mcp_server_id` — GUID of an enabled MCP server (`status: ok`).
- `tool_name` — the exact tool name on that server.
- `arguments` — object matching the tool's `input_schema` (templates like
  `{{ steps.X.output.field }}` expand before the call).

Resolve real ids in the same ONE-PLAN as the create — no names, no placeholder
GUIDs, no fallback (same rule as `integration_action`):

1. `list_mcp_servers` → pick `mcp_server_id`. If its `status` isn't `ok`, stop and
   tell the user to Test & sync / authorize it — don't build against a broken server.
2. `discover_mcp_tools(keyword=…, mcp_server_id=…)` → read the exact `tool_name`
   and its `input_schema`.
3. Plug into `config_overrides`:

```json
{
  "id": "grafana-search",
  "snippet_id": "<mcp_call snippet uuid>",
  "x": 0, "y": 0,
  "config_overrides": {
    "mcp_server_id": "628bb5a5-1066-4f70-b579-d822251895b8",
    "tool_name": "search_dashboards",
    "arguments": { "query": "OpenAI API" }
  }
}
```

Branch on `output.is_error`; read the payload downstream via
`steps.<node>.output.content` (or `.structured`). The external effect is unknown,
so the node is `RequiresCompensation`, like `integration_action`. See `Skills/mcp.md`
for the discover-before-call discipline.

## API — `fw_workflows`

`discover_operations(api="fw_workflows")` for the full list. Common ops:
`list_workflows`, `get_workflow`, `create_workflow`, `update_workflow`,
`delete_workflow`, `run_workflow`, `promote_workflow`,
`rollback_workflow`, `clone_workflow`, `diff_workflow`, `export_workflow`,
`list_versions`, `list_triggers` / `create_trigger`.

Prefer `get_workflow_details` (dedicated tool) over `get_workflow` when
explaining or editing — it returns the exact shape used by
`update_workflow_node_config`.

## Environments

- **draft** — free edit, no approval.
- **qa** — edits allowed, runs on qa-lab or production workers.
- **production** — **immutable via API**. `409 production_immutable` on
  any write. To change: `clone_workflow → draft`, edit, promote
  draft→qa→production. `draft → production` direct is forbidden.

## Running a workflow

`run_workflow` body:

- `input` (NOT `input_payload` — the DTO only binds `input`) — matches
  the workflow's `input_schema`. Read it via `get_workflow_details`.
- `target_devices` / `target_pools` — required when any node is
  `target_mode = per_device`; optional if every node is `once`.

Response: `202 Accepted` with `{ id }`. Track via `get_run_details`.

## `per_device` × `once` — the aggregated envelope

When a `per_device` node fans out across N devices, downstream sees its
output through one of two shapes:

| Producer mode | Consumer mode | `{{ steps.X.output.* }}` resolves to |
|---|---|---|
| `per_device` | `per_device` | `output.<field>` (the current device's row) |
| `per_device` | `once`       | `output.devices[N].<field>` — N is a fixed index |
| `once`       | `per_device` | `output.<field>` (broadcast) |
| `once`       | `once`       | `output.<field>` |

The most common bug is writing `{{ steps.X.output.field }}` in a `once`
consumer when X is `per_device`. The resolver doesn't find `.field` at
the root, leaves the literal `{{ … }}` in place, and the step "succeeds"
because unresolved templates aren't errors — they pass through as
gibberish into reports/emails.

If the device count isn't fixed at design time, insert a `transform`
(JMESPath) or `python_snippet` aggregator (`target_mode=once`) between
producer and consumer. The `report` snippet does NOT expand arrays —
its `rows` is a fixed list.

**Choosing transform vs python_snippet — HARD RULE:**

- `transform` (JMESPath) is **pure projection**. Use it ONLY when the
  output shape is fully known and depends on no condition: extract
  fields, rename keys, filter a list, build a payload of static shape.
- `python_snippet` is required as soon as the output depends on **any**
  condition: a `Success`/`Failure` string, including/excluding a field
  based on a flag, choosing one of two values, looping, or building
  derived data. JMESPath has **no ternary, no `if`, no conditional
  output** — generating `cond ? 'A' : 'B'` produces a runtime
  `Error(1, NN): syntax, near '?'` and the step never runs. See the
  "Transform handler limits" section in `Skills/snippets.md`.

A "classify" step (Success vs Failure, status messages, decisions about
what to forward to the next node) is **always** a `python_snippet`,
never a `transform`. Reuse the seeded classify snippet listed in your
`fw_snippets:list_snippets(type="python_snippet")` results when one
already exists.

## Generating reports inside a workflow

The `report` snippet produces the same artifact as `generate_report` —
PDF/HTML/CSV/XLSX persisted in `/admin/artifacts`, fired as a workflow
step (`Source = "workflow"`), output available via
`{{ steps.X.output.* }}`.

```json
{
  "id": "make-pdf",
  "snippet_id": "<report snippet uuid>",
  "config_overrides": {
    "format": "pdf",
    "document": { "title": "Weekly inventory", "sections": [...] }
  }
}
```

Output: `{ report_artifact_id, filename, content_type, format,
size_bytes, base64, download_url }`.

**Canonical pairing — generate a report and email it:**

```
__start__ → make-pdf → send-email → __end__

send-email: snippet.type=email_send   (native SMTP — see Skills/email.md)
  config_overrides:
    to: "ops@example.com"
    subject: "Weekly inventory"
    body: "Report attached."
    attachments:
      - file_name:      "{{ steps.make-pdf.output.filename }}"
        content_base64: "{{ steps.make-pdf.output.base64 }}"
        content_type:   "{{ steps.make-pdf.output.content_type }}"
```

Rules:
- `report` step `target_mode = once`. Per-device fan-out generates N
  identical files and explodes the output payload.
- `email_send` needs NO integration/action GUIDs — the SMTP channel is
  configured under `/email` and the default channel is used when
  `channel_id` is omitted. Note the field is `file_name` (snake),
  not `filename`.
- Don't chain two email/attachment consumers on one `report` step
  unless you want the bytes flying twice.
- For custom processing, decode `{{ steps.X.output.base64 }}` via
  `base64.b64decode(...)` inside a `python_snippet` (the `base64` module
  is allowlisted).

## Decomposition-first building (multi-system intents)

When the user's request touches **more than one external system** OR a
single system has **multiple distinct phases** (fetch / filter / update
/ notify), the agent does NOT jump straight to `create_workflow`.

### Step 1 — Propose the decomposition (with fallbacks, modes, output schemas)

One message listing each phase as a future task node. For each:

- **Header**: name + `(<snippet_type>, <target_mode>)`. `target_mode` is
  a design decision, never omitted.
- `Produce:` named output fields and types
  (`{ device_name: str, ssh_ok: bool, ssh_error: str|null }`).
- `Output contract:` — only when the user specified a literal format.
  Quote them verbatim and translate to the field shape. Snippets being
  reused MUST be checked against this in Step 3's resolution table.
- `Consumed by:` — which downstream nodes template from this output.
  Empty → the step probably shouldn't exist.
- `On failure:` — `notify` | `rollback` | `alternate`, with destination.

**Choosing `target_mode`:**

| Intent | Mode |
|---|---|
| Fan a command to each device | `per_device` |
| Send ONE summary / one report | `once` |
| Create/update ONE record per device | `per_device` |
| Generate ONE artifact aggregating N rows | `once` |

The words *"summary" / "resumen"* signal `once`; *"for each / por cada"*
signal `per_device`. Quote the matched word as justification (the user
talks in English or Spanish — detect either).

Close with an **ASCII DAG sketch** showing success edges, failure edges,
and `always` edges to `__end__`. The numbered list alone isn't enough —
the sketch is what the user is approving.

Wait for "yes" or an adjustment. No mutating tools until then.

### Step 2 — One task = one logical phase

If tempted to fuse phases, STOP and split:
- One task = one external system. Two systems → split.
- One task = one operation over a list. Two operations → chain two.
- A task taking >3 sentences to describe is too big.
- `__start__` / `__end__` are scaffolding, not phases.

### Step 3 — Clarify when ambiguous (one question per round)

Trigger (cron / webhook / manual?), target scope (all active? a pool?),
output medium (email? Slack?), failure behavior (abort? best-effort?).

### Step 4 — Mermaid for python_snippet / transform

Custom-logic snippets MUST ship `logic_diagram_mermaid`; the backend
rejects with `logic_diagram_invalid` otherwise. Built-in types (ping,
ssh, rest_call, integration_action, mcp_call, report, netconf, snmp_v3,
ansible_playbook, git) don't need one. Conventions: `Skills/mermaid.md`.

To read or write a registered repo **inside a run** (pull a config,
commit a generated report back), use a `git` node — it does the Git I/O
on the worker, so a sandboxed `python_snippet` (no `open`/`requests`)
never has to, and you don't fall back to pushing from chat. See
`Skills/git.md` § The `git` workflow node for the `config_overrides` shape
(`operation` / `repository_id` / `path` / `content` / `push`).

### Step 5 — Fallbacks (mandatory in the proposal)

Every task declares `On failure:` with one verb:

- **`notify`** (most common) — `notify-failure` node wired
  `risky-task ─failure→ notify-failure ─always→ __end__`. Default
  channel is **email** via the native `email_send` snippet. Use
  **Slack only** when the failing task IS the email send (otherwise
  the alert dies in the broken channel) or when no email channel is
  configured under `/email`.
- **`rollback`** — when the task mutated remote state. The `failure`
  edge points at a cleanup node, then chains to `notify-failure`.
- **`alternate`** — primary unreachable → try mirror. The `failure`
  edge points at the alternate; downstream joins via `always`.

Notify-failure nodes are `email_send` (or `slack_message`), never
`python_snippet` and never a hand-rolled `integration_action` against a
mail API (see `Skills/email.md`).

**Exemptions** (name explicitly in the proposal):
1. Workflow has exactly 1 task node — the run record IS the notification.
2. User explicitly asked for `best-effort` / `no notification` — quote it.
3. Pure inspection (every node `once`, only reads state).

"It's just a draft" is NOT an exemption — drafts promote without re-review.

Canonical `notify-failure` node (native `email_send` — no
integration/action GUIDs to resolve; the SMTP channel lives in `/email`
and omitting `channel_id` selects the default one):

```json
{
  "id": "notify-failure",
  "snippet_id": "<email_send snippet uuid — list_snippets(type=email_send)>",
  "x": 0, "y": 0,
  "config_overrides": {
    "to":      "{{ run.owner_email }}",
    "subject": "FlowWeaver run {{ run.id }} failed",
    "body":    "Workflow `{{ run.workflow_name }}` failed at step `{{ run.failed_step_id }}` with: {{ run.failed_step_error }}\n\nRun details: {{ run.url }}"
  }
}
```

Multiple risky nodes share one `notify-failure` sink via the
`always → __end__` edge. The send-email node is the exception — its
own Slack-routed sink.

### Step 6 — Wiring + resolution tables (MANDATORY before create)

Three artifacts, every cell backed by a real tool call cited in
`Source`. The user sees verification, not just claims.

#### Template wiring matrix

| Template path | Producer (mode) | Consumer (mode) | Resolves to | OK? |
|---|---|---|---|---|
| `steps.classify.output.netbox_payload` | classify (per_device) | create-netbox (per_device) | `output.netbox_payload` | ✓ |
| `steps.classify.output.device_name`    | classify (per_device) | email-summary (once)    | needs `output.devices[N]` | ✗ |

Any ✗ → fix before create. Either change consumer's `target_mode`, OR
add an aggregation node, OR hardcode the index `N` only if the device
count is known at design time.

#### Snippet resolution table

`target_mode` is a **snippet** property, not a node property. One snippet
shared across nodes with different required modes is impossible to
satisfy — split into variants. Naming: append the mode
(`integration_action_per_device`, `report_once`).

| Node | Snippet UUID (or `NEW`) | Snippet's live `target_mode` | Required mode | Output contract match? | Source | Action |
|---|---|---|---|---|---|---|
| classify           | `e97bbd7c-…` | `per_device` | `per_device` | n/a | `list_snippets(type=python_snippet)` | reuse |
| create-netbox      | `NEW`        | —            | `per_device` | n/a | — | **CREATE** `integration_action_per_device` |
| aggregate-results  | `fb055e02-…` | `once`       | `once`       | ✗ — existing body produces a generic table; user asked for `Workflow: X \| Device: Y \| Status: ...` per line | `get_snippet(fb055e02-…)` | **CREATE** variant |
| send-summary-email | `d555a060-…` | `once`       | `once`       | n/a | `list_snippets(type=email_send)` | reuse |

Hard rules:

1. The live mode column comes from a **real `list_snippets` /
   `get_snippet` call this turn**. Cite it. Memory is not verification.
2. Live ≠ required → action is **CREATE**, and the `create_snippet`
   call MUST be in the same ONE-PLAN block as `create_workflow`.
3. `Output contract match?` compares actual output (snippet body /
   `output_schema`) against the Step 1 contract. ✓ only after a real
   compare; ✗ → CREATE.
4. **Same UUID MUST NOT appear in two rows with different required
   modes** — the d555a060 anti-pattern.

#### GUID resolution table (for every `integration_action` node)

| Node | Field | Live GUID | Source |
|---|---|---|---|
| create-netbox      | `integration_id` | `7e2535a6-…` | `list_integrations` (name=NETBOX) |
| create-netbox      | `action_id`      | `3e885d23-…` | `list_palette_actions(integration_id=…)` |

Every `integration_action` node has TWO rows. Both come from real
lookups this turn. Placeholder GUIDs are forbidden. The lookups are
`autonomous`-tier — fire them in the same batch, no separate confirm.
`email_send` and `slack_message` nodes need NO rows here — they carry
no integration/action GUIDs (email delivers through the `/email`
channel; `channel_id` is optional and only ever a real UUID the user
named).

If any table can't be filled with verified values → re-propose with
the missing lookups, get one "yes", then run lookups + create in
one batch.

### Pre-flight checklist

Tick each box out loud in the proposal:

- [ ] Each task header has `(<snippet_type>, <target_mode>)`.
- [ ] Each task has `Produce:`, `Consumed by:`, `On failure:`.
- [ ] Wiring matrix has zero ✗ rows.
- [ ] No snippet reused across nodes with different `target_mode`.
- [ ] Snippet resolution table cites real lookups; every `live ≠ required`
      has a CREATE; every Step 1 `Output contract` is verified.
- [ ] GUID resolution table cites real lookups; zero placeholders.
- [ ] DAG sketch shows success, failure, and `always` edges.
- [ ] Every `conditional` edge has a non-empty `condition`.

Only after all boxes tick: emit ONE-PLAN and ask "yes".

## Loop-over-list workflows

FlowWeaver has no DAG-level `for-each` — `target_mode: per_device` only
fans over the **run's targets**, never an upstream step's output.

Canonical shape — **the iteration runs in a `python_snippet`** because
`integration_action` runs once per node, `report` is a document builder,
and `python_snippet` can call `integration(name).get/post/patch(...)`
in a loop and assemble both the rows and a pre-encoded CSV in one pass.
That snippet has to be **network-enabled**, or `integration()` has no network.
Only an admin can set the flag, from the UI, so put it in the plan:

```
__start__
  → fetch-list      (integration_action)
  → reconcile       (python_snippet — iterates + builds CSV)
  → make-csv-report (report — wraps the CSV; OPTIONAL if email-only)
  → send-email      (email_send — attaches the artifact)
  → __end__

  fetch-list, reconcile, make-csv-report ─failure→ notify-failure ─always→ __end__
  send-email                              ─failure→ notify-slack-failure ─always→ __end__
```

Reading `integration_action` output:

```
{{ steps.fetch.output.body }}        → raw response text (string)
{{ steps.fetch.output.data }}        → parsed JSON (use this for JSON APIs)
{{ steps.fetch.output.status_code }} → int
```

Agent-generated workflows that reference `output.results[0].name` are
wrong — the correct path is `output.data.results[0].name`.

When you hand `"rows": "{{ steps.X.output.data.rows }}"` to the
`report` snippet, the resolver substitutes the JsonElement in place —
it becomes a real 2D array, not a stringified one. Don't enumerate
`rows[0]`, `rows[1]` literally — that hardcodes length and trips the
strict resolver gate.

Python body for the iteration step: see `Skills/python_snippets.md`
("Building CSVs and email attachments").

Hard rules:
1. Never hardcode rows as scaffolds (`"PENDING_RECONCILIATION"`,
   `"TODO"`, `"placeholder"`) — `WorkflowReferenceValidator` rejects.
2. Never reference `output.body.<field>` for JSON. Use `output.data`.
3. Never `python_snippet` for the email send — see `Skills/email.md`.

## Template references are strict

Every `{{ steps.X.output.Y }}`, `{{ device.<path> }}`, `{{ input.<path> }}`,
and `{{ run.<path> }}` in `config_overrides` must resolve at run time.

**Supported template families** (anything else is a typo and the residual
scan will fail the step):

| Prefix    | Source                                              | Example                           |
| --------- | --------------------------------------------------- | --------------------------------- |
| `steps.X.output(.path)?` | upstream step output, by node id    | `{{ steps.fetch.output.data[0] }}` |
| `device(.path)?`         | current target device row           | `{{ device.name }}`, `{{ device.ip_address }}` |
| `input(.path)?`          | the run's input payload (POST body) | `{{ input.report_date }}`         |
| `run(.path)?`            | run metadata (id, workflow_name, …) | `{{ run.workflow_name }}`         |

For `run.<path>` the resolver exposes exactly these — anything else is a
typo and the residual scan will fail the step:

| Field                    | Value                                                        |
| ------------------------ | ------------------------------------------------------------ |
| `run.id`                 | the WorkflowRun uuid                                          |
| `run.workflow_id`        | the Workflow uuid                                             |
| `run.workflow_name`      | workflow name, frozen at enqueue                              |
| `run.environment`        | `draft` \| `qa` \| `production`                               |
| `run.trigger`            | `manual` \| `schedule` \| `webhook` \| `subflow`              |
| `run.started_at`         | ISO-8601 timestamp                                            |
| `run.owner_email`        | email of the user who started the run                         |
| `run.url`                | link to the run page                                          |
| `run.failed_step_id`     | node id of the failing predecessor (`""` if none)             |
| `run.failed_step_error`  | that step's error message (`""` if none)                      |

Use these in failure-notification email bodies and report titles where
it'd be awkward to thread them in via `input`.

The two `failed_step_*` fields are filled **only when the node fires from
a `failure` edge** — that's the canonical `notify-failure` wiring. A node
reached by an `always` edge gets empty strings (they still resolve, so
the step doesn't fail; the alert just won't name a step). `run.owner_email`
is empty when the run's starter has no email on file, which makes the
send fail at the integration — set the recipient explicitly via
`input` for unattended workflows if that matters.

 The executor re-scans the
result for residual `{{ ... }}` — anything left → step persists with
`Status=Failed`:

```
unresolved template references — this step references step outputs or
device fields that don't exist or didn't resolve:
  - $.document.sections[0].tables[0].rows[0][0]:
      {{ steps.dns-reconcile.output.rows[0].device_name }}
  ...
```

To avoid:
- Match node ids **case-sensitively** (`dns-reconcile`, not `DnsReconcile`).
- Match output paths **as the snippet emits them** — read the body or
  inspect a prior run's output.
- For JSON integration responses use `output.data.<field>`, not
  `output.body.<field>`.
- For dynamic arrays use a **whole-string** template
  (`"rows": "{{ steps.X.output.rows }}"`) — the resolver embeds the
  array directly. Don't enumerate indices in the config.

### Per-device envelope shortcuts

When a per_device step feeds a once-mode consumer the upstream output is
the aggregate envelope. Three convenience views beyond `devices[N]`:

```
{{ steps.ssh.output.devices_by_name['router-1'].output.results[0].output }}
{{ steps.ssh.output.first_success.device_name }}
{{ steps.ssh.output.first_success.output.stdout }}
```

`devices_by_name` is keyed by hostname (collisions get suffixed `#2`,
`#3`). `first_success` is the first device with `status == "completed"`
(lowercase, matching the `StepStatus` constants), or `null` when every
device failed (templates against `null` stay literal — chain
`| default('-')`: "every device failed" is a legitimate runtime outcome,
not a wiring mistake, which is exactly the case `default` is for).

Each `devices[i]` entry carries three name aliases — `device`,
`device_name`, `hostname` — pick whichever reads best.

### Filters

Append `| <filter>` after the path to transform the resolved value.
Filters chain left-to-right; arguments use single or double quotes:

| Filter        | Effect                                                         |
| ------------- | -------------------------------------------------------------- |
| `trim`        | Removes surrounding whitespace.                                |
| `upper`/`lower` | Case mapping.                                                |
| `truncate(N)` | Clips to N characters.                                         |
| `json`        | Serializes the value to a JSON-encoded string.                 |
| `default('x')`| Substitutes the literal `x` when path is missing/null/empty. Optional fields only — read the rule below. |
| `strip_ansi`  | Removes ANSI escape sequences (CSI, OSC).                      |
| `strip`       | `strip_ansi` + remove control chars + `trim`.                  |

#### `default` is not a way to silence the residual scan

`default` is the only filter that fires when the path doesn't resolve;
the rest pass through untouched. That also makes it the only filter that
can hide a bug, so it has exactly one legitimate use: **a field that is
genuinely optional at runtime**. `first_success` when every device
failed, an `error` string that is empty on success, a NetBox attribute
some devices don't set — real values that are legitimately absent on a
correctly-wired workflow.

**Never add `default` to make a residual error go away.** If the step
needs the field, a missing path means the producer doesn't emit it — or
you typed the wrong node id, or the wrong `target_mode` — and the step
SHOULD fail. That scan is the only thing standing between a wiring
mistake and a report full of confident zeros.

Smell test: if the answer to *"is `'-'` an acceptable value to show the
user in this field?"* is no, `default` is the wrong tool. Fix the
reference or fix the producer.

Real incident: a `report` node's four stat tiles pointed at fields the
aggregator never emitted (`total` / `online` / `offline` /
`generated_at`, against an aggregator producing `counts.*`). Three
carried `| default('0')` and rendered a clean, plausible report claiming
0 devices reachable; only the fourth — the one without a `default` —
failed the residual scan and exposed the mismatch. Without that single
honest reference the workflow would have kept emitting zeros forever.

```json
{
  "subject": "Workflow {{ run.workflow_name }} — {{ steps.x.output.title | default('(no title)') }}",
  "snippet": "{{ steps.x.output.body | strip | truncate(120) }}",
  "raw_payload": "{{ steps.x.output | json }}"
}
```

### SSH output is ANSI-stripped by default

`steps.<ssh-node>.output.stdout` and each `results[i].output` are
sanitised at the runner: ANSI CSI/OSC sequences and C0 control chars
(except `\t \n \r`) are removed before the value reaches downstream
templates. Useful for Nokia SR-OS / Cisco IOS-XR with paging / `screen`
sessions where escape codes used to leak into NetBox custom_fields and
email bodies. The raw bytes are preserved under `stdout_raw` and
`results[i].output_raw` for forensic use. Set `preserve_ansi: true` in
the SSH node's `config_overrides` to disable stripping.

## Subflows (FR-025)

Embed a workflow as a single node:

```json
{
  "id": "backup-all",
  "snippet_id": "subflow",
  "x": 0, "y": 0,
  "type": "subflow",
  "config_overrides": {
    "subflow_workflow_id": "<child workflow uuid>",
    "subflow_name": "<cached display name, optional>",
    "subflow_environment": "<cached env, optional>"
  }
}
```

- `snippet_id` MUST be the literal `"subflow"`.
- `config_overrides.subflow_workflow_id` is the **only** key the engine
  reads. `subflow_name` / `subflow_environment` are display hints the
  editor caches to render the canvas without re-fetching; the runtime
  ignores them.
- Child must be tagged `metadata.is_subflow = true` to appear in the
  editor picker (toggle under workflow's **More → Reusable as subflow**).
  Untagging doesn't break existing parents — they reference by id.
- The DAG parser detects transitive cycles (`A → B → A`) at enqueue time,
  and the orchestrator checks again at dispatch against the chain of runs
  above it — which catches a graph edited after the run started.

**Input binding** — child sees: parent run's `input_payload` shallow-
merged with the subflow node's `config_overrides` (minus
`subflow_workflow_id`), with `{{ steps.X.output.Y }}` resolved against
the parent's completed steps. The child can NOT reach back into the
parent — bind every value via this payload.

**Output binding** — the subflow node's output describes the CHILD RUN:

```json
{ "run_id": "…", "status": "completed", "final_state": "completed",
  "steps": { "<child-node-id>": { …that step's output… } } }
```

So a child step's output is read as
`{{ steps.backup-all.output.steps.<child-node>.<field> }}`.

**This changed on 2026-08-29.** It used to be the steps map alone at the top
level — `{{ steps.backup-all.output.<child-node>.<field> }}`, one level
shallower. **A template written against the old shape must gain `.steps`.**
The new shape also lets you branch on the child's verdict, which the old one
could not express at all: `{{ steps.backup-all.output.final_state }}`.

**Reversibility crosses the boundary.** The subflow node inherits the
STRICTEST idempotency tier of whatever the child actually executed. A child
that sent an email makes the parent node non-reversible, which keeps it out
of the parent's rollback plan — as it should, since nothing can unsend it.
You cannot declare that away on the parent: the tier may be raised, never
lowered.

**Failures are named.** A subflow node that fails carries an `error_code`:
`subflow_failed` (the child ran and failed — go read the child run),
`subflow_missing` (no workflow named, or the named one does not exist), or
`subflow_cycle` (would re-enter a workflow already running above it; nothing
of the child ran). All three fail the STEP, so a `failure` edge out of the
subflow node still fires and can compensate.

**Targets** — subflows inherit parent's `target_devices` /
`target_pools`. If the child needs different targets, model it as
`integration_action`, not subflow.

**Don't use a subflow when**: child has 1–2 nodes (inline cheaper);
need multiple named artifacts for different parent steps (subflow
returns one aggregated object — refactor as siblings).

## Editing a node via chat

For quick fixes: `update_workflow_node_config(workflow_id, node_id,
config_overrides)` — shallow-merges and audits. Cannot create or
delete nodes. For structural changes use `update_workflow` with full
`nodes` + `edges`.
