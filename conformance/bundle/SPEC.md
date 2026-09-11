# workflow.v1 — Interchange bundle (v3)

Status: draft · Contract 1.1.0-draft · Oracle: flow-weaver `fw@1a11ea3` (2026-08-26)

The bundle is the **only** portable form of a workflow. A workflow.v1 document
(`{nodes, edges}`) is meaningful inside one instance; the bundle gives every
identifier in it a portable identity and carries every definition the receiving
instance needs to run it. A bundle that imports must run; a bundle that cannot run
must be refused at import, naming what is missing. Nothing may be guessed.

This document supersedes the informal v2 shape emitted by both products since
2026-08-05. v2 remains **readable** by every conforming importer; conforming
exporters emit v3.

## 1. Root

```jsonc
{
  "schema_version": "v3",
  "kind": "flow_weaver.workflow_bundle",   // the marker; never detect by shape
  "exported_at": "2026-08-27T12:00:00Z",
  "exported_by": { "product": "nashira" | "flow-weaver", "version": "..." },   // informational
  "requires": { ... },                     // §2 — what the importer must support
  "workflow": { ... },                     // §3
  "nodes": [ ... ],                        // workflow.v1 nodes, verbatim (§4 for reference keys)
  "edges": [ ... ],                        // workflow.v1 edges, verbatim
  "dependencies": { ... },                 // §5
  "triggers": [ ... ]                      // §6
}
```

`kind` values a conforming importer MUST accept: `flow_weaver.workflow_bundle`,
`nashira.workflow_bundle`, `netora.workflow_bundle`. Exporters SHOULD emit
`flow_weaver.workflow_bundle`.

`schema_version` values a conforming importer MUST accept: `v2`, `v3`. Any other
value is refused with `bundle_version_unsupported` naming the version. A v2 bundle
is read as a v3 bundle with `requires` inferred (§2.3) and no `triggers`.

Unknown members at any level are ignored (forward compatibility), except inside
`requires` where an unknown capability name is refused (§2.2).

## 2. `requires`

The bundle declares what it needs. This is how an importer refuses precisely
instead of failing at run time, and how it degrades **with a note** instead of
silently.

```jsonc
"requires": {
  "snippet_types": ["ssh", "transform", "slack_message"],
  "capabilities": ["subflow", "template_filters", "per_device_scope"],
  "secrets": [ { "ref": "${secret:store:netops-ssh:password}", "used_by": ["show-version"] } ]
}
```

### 2.1 `snippet_types`
Every `type` of every snippet in `dependencies.snippets` (and of every snippet in
every nested workflow). The importer refuses with `bundle_dependencies_missing`
when its handler registry lacks one, naming the type and the snippets that use it.

### 2.2 `capabilities`
Closed vocabulary. An importer that does not implement a listed capability MUST
refuse (`bundle_capability_unsupported`) unless the capability is marked
*degradable* below, in which case it MUST import with a note.

| capability | meaning | degradable |
|---|---|---|
| `subflow` | at least one node has `type: "subflow"` | no |
| `template_filters` | a template uses `\| filter` (templates/SPEC.md §4) | no |
| `run_namespace` | a template uses `{{ run.* }}` | no |
| `per_device_scope` | a `per_device` consumer reads a `per_device` producer's output by field (templates/SPEC.md §6) | no |
| `max_parallel` | a snippet declares `max_parallel > 1` | yes — walked serially |
| `per_pool` | a snippet declares `target_mode: "per_pool"` | yes — as `per_device` |
| `conditional_edges` | an edge has `type: "conditional"` | no |
| `rest_catalog` | a `rest_call` node uses the catalogued form (`source` / `operation_id`) rather than the raw one | no |
| `python_network` | a python snippet had `network_enabled: true` at the source | yes — never granted; noted |
| `triggers` | the bundle carries `triggers` | yes — skipped with a note |

### 2.3 Inference from v2
When reading a v2 bundle, the importer computes `snippet_types` from the
dependencies and `capabilities` from the nodes/snippets it can see (`subflow`,
`conditional_edges`, `max_parallel`, `per_pool`, `python_network`). Template-level
capabilities are inferred by scanning `config_overrides` strings for `|` inside
`{{ }}` and for `{{ run.`.

### 2.4 `secrets`
Every `${secret:<source>:<name>:<field>}` reference found in any node's
`config_overrides` or any snippet's `code`/`input_schema` defaults. The importer
does not resolve them; it reports the ones that do not exist locally as
**notes** (not refusals), so the operator or the agent can create them before the
first run. The bundle never carries a secret value.

## 3. `workflow`

```jsonc
"workflow": {
  "name": "audit-and-notify",
  "description": "…",
  "environment": "qa",          // informational; the import always lands in draft
  "input_schema": { … } | null,
  "metadata": { … } | null      // metadata.is_subflow travels; nothing else is interpreted
}
```

The source's `workflow_id`, `version`, `schema_hash`, promotion history, runs,
tests and RBAC never travel.

## 4. Node reference keys (portable identity)

A node's `config_overrides` may reference things that live outside the graph.
Inside a bundle, every such reference MUST use the **portable key** below. The
legacy keys are accepted by importers **only** when `dependencies` carries the
mapping that makes them translatable; otherwise the import is refused naming the
node and the key.

| thing referenced | portable key (canonical) | value | legacy keys accepted on import |
|---|---|---|---|
| snippet | `snippet_id` (node field) | GUID, remapped through `dependencies.snippets[].id` | — |
| integration | `integration` | slug of the integration | `integration_id` (GUID → via `dependencies.integrations[].id`) |
| integration action | `action` | action `name` within the integration | `action_id` (GUID → via `dependencies.integrations[].actions[].id`) |
| MCP server | `server` | server name | `mcp_server_id` (GUID → via `dependencies.mcp_servers[].id`) |
| credential | `credential` | credential name | `credential_id` (GUID → via `dependencies.credentials[].id`) |
| sub-workflow | `subflow_workflow_id` | GUID, remapped through `dependencies.workflows[].id` | — |
| git repository | `repository` | repository name | `repository_id` (GUID → via `dependencies.repositories[].id`) |

Exporters MUST write the canonical key, translating their local id on the way out,
and MUST NOT emit the legacy id key. Importers still accept the legacy keys —
v2 bundles and hand-authored files carry them — and when both are present the
canonical one wins.

The value written MUST be the portable identity named above (the integration's
slug, the action's name, …), never the exporting instance's display name. A
product whose own nodes store something else — a local id, a local name —
translates on the way in and back on the way out. What it stores is its business;
what it puts on the wire is this.

**Why ids may not travel beside names.** The node objects are hashed
(`canonicalization/SPEC.md`), and that hash is what §8's round trip compares. A
local GUID in `config_overrides` is meaningful only on the instance that wrote it,
so carrying one makes the hash instance-specific: the same workflow exported from
two instances would fingerprint differently, and a bundle that crossed products
could never come back equal to itself. Same-instance re-import is not a good enough
reason — it is already exact, because the importer resolves the same names to the
same rows.

`snippet_id` and `subflow_workflow_id` stay GUIDs because they are structural
(`workflow.v1.schema.json` requires a GUID or a sentinel) and because they are
always remapped: the bundle carries the full definition of what they point at.

## 5. `dependencies`

Everything a node references, by portable identity, with **full definitions for
what is safe to recreate** (snippets, sub-workflows) and **identity only for what
holds credentials** (integrations, MCP servers, credentials, repositories).

```jsonc
"dependencies": {
  "snippets":      [ { "id", "slug", "name", "type", "description", "code", "script_language",
                       "input_schema", "output_schema", "target_mode", "max_parallel",
                       "timeout_seconds", "idempotency", "retry_policy",
                       "logic_diagram_mermaid", "network_enabled" } ],
  "integrations":  [ { "id", "slug", "name", "type", "base_url",
                       "actions": [ { "id", "name", "method", "path" } ] } ],
  "mcp_servers":   [ { "id", "name", "transport" } ],
  "credentials":   [ { "id", "name", "type", "auth_method", "username" } ],
  "repositories":  [ { "id", "name", "remote_url" } ],
  "workflows":     [ { "id", "name", "description", "input_schema", "metadata",
                       "nodes", "edges" } ]          // sub-workflows, flattened (§5.4)
}
```

### 5.1 Resolution order (all kinds)
`slug` (where the kind has one) → exact case-insensitive `name` → **refuse**.
Never fuzzy. A near miss binds a workflow to the wrong system with that system's
credentials.

### 5.2 Snippets
Reused when found (by slug, then name); otherwise **created from the carried
definition**, `verified = false`, `network_enabled = false` (with a note when the
source had it on). `retry_policy` is the canonical shape (execution/SPEC.md §3);
an importer whose engine uses another shape translates on import. `max_parallel`
and `target_mode: per_pool` degrade per §2.2.

### 5.3 Integrations, MCP servers, credentials, repositories
Identity only. **Secrets never travel**: no auth config, headers, tokens, private
keys, webhook secrets. Missing → `bundle_dependencies_missing` naming the item and
its type (and `base_url` / `remote_url` when known, so the operator can tell which
one to create). An integration found by slug under a different local name is
usable — the node references the slug — but the importer notes the name mismatch.

### 5.4 Sub-workflows
Every workflow reachable through `subflow` nodes, transitively, appears **once** in
`dependencies.workflows`, keyed by its source id. Cycles are refused
(`bundle_subflow_cycle`). Their own snippets and references are merged into the
same `dependencies` sections and `requires`. On import, sub-workflows are created
first (as drafts, `metadata.is_subflow = true`, reusing an existing one only on an
exact name match **and** identical canonical hash of nodes+edges), then every
`subflow_workflow_id` in the parent and in the other sub-workflows is remapped.

## 6. `triggers`

```jsonc
"triggers": [
  { "name": "nightly", "type": "cron", "description": "…",
    "cron_expression": "0 3 * * *", "timezone": "America/Bogota",
    "input_schema": { … }, "input_defaults": { … },
    "enabled": false, "notify_on": ["failed"] },
  { "name": "on-push", "type": "webhook", "route": "abc123",
    "allow_unsigned": false, "allow_target_override": true,
    "input_schema": { … }, "input_defaults": { … }, "enabled": false }
]
```

Portable types: `cron`, `webhook`. Other types (`schedule`, `event`, git hooks)
are exported as-is and **skipped with a note** by an importer that lacks them.

What never travels: the webhook HMAC secret, `target_devices` (local ids), run
statistics, and **`notification_webhook_url`**. That last one was in this
document's first draft and it was a mistake: an incoming-webhook URL for Slack,
Teams or PagerDuty carries its token in the path, so the URL *is* the credential.
It is bearer material wearing a field name that reads like configuration — which
is exactly how a secret escapes a review. The receiving instance supplies its own;
the importer notes that a notification target was declared and dropped, so the
operator knows to re-point it.

On import every trigger is created **disabled**, with a **fresh secret**, and no
targets — the note tells the operator what to set before enabling. `route` is kept
when free on the receiving instance, otherwise regenerated and noted.

## 7. Import outcome

Exactly one of:

- **refused** — nothing is created; the error names every missing dependency,
  unsupported capability or untranslatable key at once (`bundle_dependencies_missing`,
  `bundle_capability_unsupported`, `bundle_version_unsupported`, `bundle_incomplete`,
  `bundle_subflow_cycle`, `bundle_reference_untranslatable`).
- **imported with notes** — the workflow (and sub-workflows, snippets, triggers)
  exist; `import_notes[]` lists every degradation and every secret/trigger the
  operator still has to configure. Silence means nothing was degraded.

The imported workflow always lands in `draft`, `version 1`, with no simulation or
test evidence. The importer runs the same schema + reference + DAG validation as a
hand-authored write.

## 8. Conformance

Family `bundle` (vectors/bundle/): each vector is a v3 bundle plus the expected
outcome (`refused` with codes, or `imported` with the expected canonical hash of
`nodes`+`edges` after remap, the expected notes, and the expected set of created
kinds). The round-trip vector exports from the oracle, imports here, exports
again, and asserts the canonical hash and the `requires` block are unchanged.

The hash compared there is the one over the **bundle's own** `nodes` + `edges`,
not over the imported workflow row. The two are deliberately different things: the
stored row may carry whatever local vocabulary the product runs on (ids, local
names), while the wire form carries portable identities only — which is exactly
what §4 forbids ids for. Comparing stored rows would make the vector assert that
two products store workflows identically, which is not the contract and is not
true; comparing wire forms asserts the only thing that matters, that the workflow
survived the crossing intact.

`snippet_id` and `subflow_workflow_id` are the exception the round trip must
tolerate: they are remapped to the receiving instance's rows by design, so the
comparison normalizes them (`normalize: ["remap:snippet_ids", "remap:workflow_ids"]`)
rather than expecting equality.
