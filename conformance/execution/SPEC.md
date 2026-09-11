# workflow.v1 — Execution semantics a portable workflow relies on

Status: draft · Contract 1.1.0-draft · Oracle: flow-weaver `fw@1a11ea3`

The graph says what runs after what. This document fixes the behaviour a workflow
author may assume in either product: how a node's outcome is classified, when a
failed step is retried, what a `subflow` node does, and what an importer does
with triggers. Everything here is already the oracle's behaviour unless marked
**profile** (a documented, declared difference).

## 1. Node outcome and edge firing
A step ends `changed` | `no_change` | `failed` | `skipped`. Edge firing:
`always` → fires; `success` → source is `changed` or `no_change`; `failure` →
source is `failed`; `conditional` → source succeeded **and** the condition holds
(templates/SPEC.md §9). A node runs when at least one incoming edge fired; in-degree
0 nodes always run. With `stop_on_failure` (always on in a run started through the
API) the walk stops at the first `failed` node whose failure is not consumed by a
`failure` edge.

Run status: `completed` | `failed`. `final_state`: `completed` | `rolled_back` |
`failed` (a non-reversible change happened before the failure) | `refused` (nothing
ran).

## 2. Idempotency
Three tiers: `idempotent` < `requires_compensation` < `non_reversible`. The handler
declares a floor; the snippet may declare a stricter tier, never a weaker one; an
unannotated snippet is `requires_compensation`. Rollback analysis: a workflow is
reversible iff it contains no `non_reversible` node; at run time the rollback plan
is the reversible `changed` nodes in reverse execution order.

**Single source.** A node's effective tier is a property of its **snippet**
(bundle definition), not of `config_overrides`. A `config_overrides.idempotency`
key is an accepted *stricter-only* override — it can raise the tier, never lower
it — and importers note it.

## 3. Retry policy (canonical shape — FW is the oracle)
```jsonc
"retry_policy": {
  "max_retries": 0,              // extra attempts after the first; 0 = no retry
  "initial_delay_seconds": 5,
  "backoff": "exponential",      // exponential | linear | fixed
  "max_delay_seconds": 300
}
```
Delay before retry `k` (1-based): exponential `initial · 2^(k-1)`, linear
`initial · k`, fixed `initial`; clamped to `max_delay_seconds`. A malformed policy
means **no retry**.

Legacy Nashira shape `{ max_attempts, delay_seconds, backoff }` is accepted on
read: `max_retries = max_attempts − 1`, `initial_delay_seconds = delay_seconds`,
`max_delay_seconds = 30`. Exporters write the canonical shape.

**Gates.** A retry happens only when the handler marked the failure `retryable`.
**Profile (Nashira):** additionally, only `idempotent` snippets are retried — a
`requires_compensation` step with a retry policy is surfaced, not retried — and
attempts are capped at 5 / delay at 30 s. A bundle cannot change this; it is
declared here so authors know.

## 4. Fan-out
`target_mode`: `once` (one execution) | `per_device` (one execution per target
device; outputs aggregated per templates/SPEC.md §6). `per_pool` and `max_parallel`
are oracle extensions, degradable at import (bundle/SPEC.md §2.2). A `per_device`
step in a run with no targets is `failed` with `no_targets`, never silently
skipped. Every target is checked against the device's environment policy before
execution; a refused device is reported as a failure of that device, not skipped.

## 5. Subflows — capability `subflow`
A node with `type: "subflow"` and `snippet_id: "subflow"` runs another workflow.

| field | meaning |
|---|---|
| `config_overrides.subflow_workflow_id` | GUID of the child workflow (remapped at import) — required; missing → step `failed` with `subflow_missing` |
| other `config_overrides` keys | merged **shallowly over** the parent run's input to form the child's input (`config_overrides` wins), then template-resolved in the parent's context |

Semantics the author may rely on:
- The child is a real run of the child workflow: it appears in the run list with
  `parent_run_id` set and `trigger: "subflow"`; the parent's step records
  `child_run_id`.
- The child inherits the parent's target devices and environment policy scope.
- The child runs the **current** version of the child workflow in the parent's
  environment; there is no version pinning (oracle behaviour, documented).
- The step's `output` is `{ "run_id", "status", "final_state", "steps": { "<node_id>": <output> } }`;
  the step is `changed` if any child step changed, `failed` if the child run
  failed (its `error` is the step's error), else `no_change`.
- The step's tier is the strictest tier among the child's nodes.
- Cancellation of the parent cancels the child. Depth is capped at 8; a cycle
  (a workflow reaching itself) fails the step with `subflow_cycle` before anything
  runs.
- A residual `{{ … }}` in the merged child input fails the step
  (`unresolved_template`) rather than propagating into the child.

## 6. Environments and promotion
`draft → qa → production`; only `draft` is editable; an import always lands in
`draft`. `draft → qa` requires a fresh, successful simulation of the current hash.
Promotion gates beyond that (organisational policies, four-eyes, acceptance tests)
are **product policy**, not contract.

## 7. Triggers on import
Created **disabled**, without targets, with a fresh secret (bundle/SPEC.md §6).
Enabling a trigger is an explicit local act. A cron trigger's `timezone` is an IANA
name; an importer that cannot resolve it keeps the trigger disabled and notes it.

## 8. Audit
Every state-changing node emits an `audit.v1` event `{ schema, event_id,
workflow_id, schema_hash, node_id, op, idempotency, actor, tenant_id, timestamp,
result }`. `op` is the node's `snippet_id`. `tenant_id` is part of the schema even
where a product has no tenants (emitted as the nil GUID).

## Conformance
Family `executor` (vectors/executor/): goldens captured from the oracle — for a
given bundle + input + fake device set: the ordered list of `(node_id, result)`,
the `rollback_plan`, `final_state`, and for subflow nodes the child's `(node_id,
result)` list. Family `gate` already covers §6.
