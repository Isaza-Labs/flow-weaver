# Subflows

A subflow is a workflow you call from another workflow. It is the unit
of reuse: build the "drain a port", "open a maintenance window",
"export inventory" flow once and call it from anywhere.

## Anatomy

| Concept | Where it lives |
|---|---|
| Subflow definition | A regular `Workflow` row, tagged `metadata.is_subflow = true` |
| Subflow listing | `/subflows` (UI) or `GET /api/workflow/subflows` (API) |
| Caller | Any workflow node with `type = "subflow"` and `snippet_id = "subflow"`, whose `config_overrides.subflow_workflow_id` holds the subflow's `workflow_id` |
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
2. Pick a subflow from the dropdown (populated from `GET /api/workflow/subflows`,
   all environments).
3. Map inputs: the dialog previews the subflow's `input_schema` so you can see
   what it expects.
4. Downstream nodes read the result as `steps.<node_id>.output`, an object with
   the child's `run_id`, `status`, `final_state`, `error` (when it failed) and
   `steps` — each child step's output keyed by its node id, e.g.
   `{{ steps.<node_id>.output.steps.<child_node_id>.<key> }}`.

At runtime:

- A child `WorkflowRun` is created with `ParentRunId` set to the parent run.
  Its input is the parent run's input merged with the subflow node's
  `config_overrides`, and it inherits the parent's target devices and pools.
- The child run advances independently and completes (or fails) the
  parent's subflow node. A failed child fails the node with `subflow_failed`;
  a reference to a workflow that does not exist fails it with
  `subflow_missing`.
- Cancelling the parent run stops the parent's own steps; a child run that
  has already started is a separate run and must be cancelled on its own.

## Conventions

- **Inputs**: define an explicit `input_schema` on the subflow so callers can
  see what it expects. The child input is not validated against the schema at
  run time, so check required fields in the subflow itself.
- **Outputs**: end the subflow with a node that emits a JSON object the
  caller can consume.
- **Naming**: prefix subflow workflow names with their domain
  (`net.drain_port`, `inv.refresh`) so they are easy to find in the picker.
- **Promotion**: subflows are promoted independently. The node picker lists
  tagged subflows from every environment; the `/subflows` page and
  `GET /api/workflow/subflows?environment=<env>` can filter by environment.

## Sharing a workflow that calls subflows

A `bundle` export carries them. Every workflow reachable through `subflow`
nodes travels with the parent, transitively, once each, under
`dependencies.workflows` — with its own snippets and references merged into the
same `dependencies` block, so the receiving instance has everything it needs in
one file.

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

## Current limitations

- Promoting a parent does not check the environment of the subflows it calls
  (e.g. a parent promoted to `production` while a subflow is still in `qa`).
- A parent always calls the subflow's current version; pinning a subflow
  `Version` is not supported.
- The run monitor does not link a parent's subflow step to its child run; the
  child's id is in the step output (`run_id`) and the child is listed under
  runs like any other run.
