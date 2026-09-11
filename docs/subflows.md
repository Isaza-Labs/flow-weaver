# Subflows

A subflow is a workflow you call from another workflow. It is the unit
of reuse: build the "drain a port", "open a maintenance window",
"export inventory" flow once and call it from anywhere.

## Anatomy

| Concept | Where it lives |
|---|---|
| Subflow definition | A regular `Workflow` row, tagged `metadata.is_subflow = true` |
| Subflow listing | `/subflows` (UI) or `GET /api/workflow/subflows` (API) |
| Caller | Any workflow node with `type = "subflow"` referencing the subflow's `workflow_id` |
| Runtime link | `WorkflowRun.ParentRunId` points the child run at the parent run |

The schema does not require a special table. A subflow is just a
workflow whose author has flagged it as reusable.

## Tagging a workflow as a subflow

1. Open the workflow under `/workflows/<id>`.
2. Open the metadata panel.
3. Tick **Reusable as subflow** (or set `metadata.is_subflow = true` via
   the JSON editor for advanced cases).
4. Save. The workflow now appears at `/subflows` and inside the subflow
   node's picker.

You can untag at any time. Existing parent workflows that reference the
subflow will keep working — the reference is by id, not by tag.

## Calling a subflow

In the editor:

1. Add a node and choose the **Subflow** type.
2. Pick a subflow from the dropdown (populated from `GET /api/workflow/subflows`).
3. Map inputs: the subflow's `input_schema` defines what it expects.
4. The subflow's outputs become available downstream as
   `steps.<node_id>.output.<key>`.

At runtime:

- A child `WorkflowRun` is created with `ParentRunId` set to the parent run.
- The child run advances independently and completes (or fails) the
  parent's subflow node.
- Cancellation propagates: cancelling the parent cancels its children.

## Conventions

- **Inputs**: define an explicit `input_schema` on the subflow. Callers
  cannot pass arbitrary fields — the editor validates against the schema.
- **Outputs**: end the subflow with a node that emits a JSON object the
  caller can consume.
- **Naming**: prefix subflow workflow names with their domain
  (`net.drain_port`, `inv.refresh`) so they are easy to find in the picker.
- **Promotion**: subflows are promoted independently. A subflow tagged
  in `qa` only appears in the picker for callers also in `qa`.

## Example seed

Bundled example (loaded by `CatalogReseedService` on first boot):

| Name | Environment | Purpose |
|---|---|---|
| `net.drain_port` | `qa` | Drain traffic from a port and verify with a `show interfaces` poll. |
| `inv.refresh_devices` | `qa` | Pull NetBox inventory and reconcile against the local cache. |

> Open work: ship the seed JSON files under `flow_weaver_backend/Skills/`
> and have `DevSeedService` import them on first run. Tracked as a
> follow-up to S14.1.

## Sharing a workflow that calls subflows

A `bundle` export carries them. Every workflow reachable through `subflow`
nodes travels with the parent, transitively, once each, under
`dependencies.workflows` — with its own snippets and references merged into the
same `dependencies` block, so the receiving instance has everything it needs in
one file.

This was not always true: the bundle used to carry the parent alone. It imported
cleanly, the subflow node kept pointing at a GUID that does not exist on the
receiving instance, and the step failed with `subflow_missing` on the first run —
the workflow looked shared and was not.

On import the children are created **first**, callees before callers, as drafts
tagged `metadata.is_subflow = true`; then every `subflow_workflow_id` is
rewritten to the row that now exists. An existing local workflow is reused only
when its name matches exactly **and** its nodes and edges are identical —
anything looser would bind a caller to a workflow that merely shares a name. All
of it happens in one transaction, so a validation failure on the third child
leaves nothing behind.

A cycle (a workflow that reaches itself) is refused at export and again on read,
with `bundle_subflow_cycle`. It could never run, so there is no reason to ship it.

See `docs/workflow-bundles.md` for the rest of the format.

## Open follow-ups

- Editor warning when promoting a parent past a subflow's environment
  (e.g. promote-to-production when subflow is still in `qa`).
- Subflow versioning: should a parent pin a subflow's `Version`, or
  always call the latest? Default today is "always latest"; pinning is
  not implemented.
- Drill-down navigation from a parent step to its child run in the
  monitor view.
