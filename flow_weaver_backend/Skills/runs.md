# Skill: runs & troubleshooting

Every workflow execution is a **run** with a status lifecycle and a set of
**step runs** (one per DAG node). Runs are the primary diagnostic surface
— "Fix with AI" and "why did this fail?" flows both start from a run id.

## Dedicated tools (prefer over specs)

| Tool | Use when |
|---|---|
| `get_run_details(run_id)` | First call after any user mention of a run id. Returns header + step summary + failure count. |
| `get_step_logs(step_run_id)` | Pull logs + error + input/output for one step. Logs are truncated to 8 KB — ask the user for the raw file if that isn't enough. |
| `get_workflow_details(workflow_id)` | Needed when the user asks to fix a node — gives you the DAG + `config_overrides` for each. |
| `update_workflow_node_config(workflow_id, node_id, config_overrides)` | Apply a fix to exactly one node. Production immutable (409). |

Use the `fw_runs` spec only when you need list-all-runs pagination.

## Status tokens

| Status | Meaning |
|---|---|
| `pending` | enqueued, worker hasn't claimed |
| `running` | at least one step is executing |
| `completed` | all steps ok |
| `failed` / `failure` | at least one non-recoverable step failed |
| `skipped` | the walk never reached this node. Three different diagnoses live here: a condition that did not hold, a branch nobody took, or **the run stopped at an earlier failure** — check whether any step failed before deciding which |
| `timeout` | step exceeded its timeout |

A run with `completed` can still have individual steps in `failed` if the
DAG routes around failures — inspect `get_run_details`'s `failed_count`.

When a run has a failed step AND skipped steps, do not describe the skipped ones as "not
reached yet" or as a second problem. They are the stop working: the run halted at the failure
because no `failure` edge left that node. The fix a user usually wants is a `failure` edge, not
a retry.

**Every node of the graph now has a step row.** Nodes the walk never reached
are recorded `skipped` rather than being absent, so "this node is missing
from the run" is no longer a thing you have to interpret — if a node has no
row at all, something is wrong with the run record itself, not with the DAG.

## Did it change anything — read this before saying a run is safe

`status` says whether a step SUCCEEDED. It does not say whether it DID
anything, and those are different questions: a `GET` that returned 200 and a
`DELETE` that returned 200 are both `completed`.

Each step carries a separate `changed` flag beside its status, and the run
carries the summary:

| Field | Meaning |
|---|---|
| `changed` (per step) | `true` the step mutated something, `false` it ran and found nothing to do, `null` recorded before this existed |
| `final_state` (per run) | `completed` \| `rolled_back` \| `failed` |
| `changed_count` | how many steps changed something |
| `rollback_plan` | the reversible changed nodes, in reverse execution order |

**`rolled_back` does NOT mean anything was undone.** Nothing executes the
plan. It means *every change this run made could have been undone*, and
`failed` means *at least one could not* — something is still changed out
there. When you report a failed run to a user, that distinction is the most
important sentence you will write: lead with it.

Never describe a `rolled_back` run as "cleaned up" or "reverted". It was not.

## Error codes

A failed step may carry `error_code` beside the human-readable `error`. The
code is what you branch on; the message is written for a person and changes.

| Code | What to do about it |
|---|---|
| `subflow_failed` | the child run ran and failed — go read the child run |
| `subflow_missing` | the node names no workflow, or names one that does not exist. There is no child run to read; fix the node |
| `subflow_cycle` | starting the child would re-enter a workflow already running above it. Nothing of the child ran |
| `unresolved_template` | a `{{ … }}` reference did not resolve. The message names which |
| `change_undeclared` | the snippet's type cannot tell whether the step changed anything and nobody declared it — see the snippets skill |

## Diagnostic playbook

1. Call `get_run_details(run_id)`. Read `failed_count` + the step
   summary. Identify which `step_run_id` failed first in time.
2. Call `get_step_logs(step_run_id)`. Read `error` and the tail of `logs`.
3. Classify the failure:
   - **Credential / auth**: the handler says "401", "permission denied",
     "SSH auth failed". Point user at `/credentials` or `/admin/secrets`.
   - **Network / timeout**: "connection refused", "dial tcp", "host
     unreachable". Suggest checking device reachability with a ping node.
   - **Config shape**: "input.X is required", "parameter missing". Offer
     to fix via `update_workflow_node_config`.
   - **Handler logic**: a script error, template rendering, bad JMESPath.
     Explain the concrete error, propose the fix, confirm before applying.
   - **Undeclared change**: the error names a snippet and asks for
     `changes`. Not a bug in the step — the snippet's type cannot tell
     whether it changed anything, and nobody said. Fix with
     `update_workflow_node_config(... {"changes": true|false})`, or set
     `changes_state` on the snippet if the action lives in its code.
4b. If the run failed, read `final_state` before summarising. `failed` means
   something is still changed and un-reversed; say so first.
4. Present the diagnosis in markdown:
   - `### Root cause`
   - `### Proposed fix`
   - a code block with the exact new config
   - a plan-style confirmation before calling `update_workflow_node_config`.

## Fix-with-AI entry point

When the user arrives from "Fix with AI", the chat pre-loads with:

- `run_id`, `node_id`, error preview, workflow id, service def id.

Skip `get_run_details` if the preview already tells you the failing node,
jump straight to `get_step_logs` + `get_workflow_details` to shape the fix.

## Don't do

- Don't rerun a workflow yourself to "test the fix" unless the user asked.
  The user owns the decision to execute against devices.
- Don't delete failed runs to "clean up". Runs are the audit trail.
- Don't edit a production workflow node. Use clone → qa → promote.
