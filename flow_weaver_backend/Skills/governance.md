# Skill: governance — plans, approvals, audit

Flow Weaver has a `WorkflowPlan` governance layer for cases where a
change **needs human review before it becomes a real workflow**. Most
day-to-day asks don't need it — and the backend now enforces this.

## When to use a plan vs. creating the workflow directly

**`create_workflow_plan` is restricted by the backend to `qa` and
`production` target environments.** If you call it for a draft-bound
request, the handler returns `error: "draft workflows must use
fw_workflows:create_workflow directly, not the plan workflow"`. That
is intentional — the plan flow exists for human-review gates, not for
day-to-day creation.

Routing rules:

| User intent | Correct path |
|---|---|
| "create a workflow that does X" | `fw_workflows:create_workflow` (draft). One tool call, single confirmation. |
| "I want this reviewed before it lands" | `create_workflow_plan` (qa-bound) → `submit_plan_for_approval` → (admin) `approve_plan` → `build_plan`. |
| "land this directly in production" | `create_workflow_plan` (production target). Admin-only `build_plan`. |
| "promote qa-workflow-X to production" | `fw_workflows:promote_workflow`. No plan needed. |

**Do NOT default to a plan for a draft workflow.** `create_workflow_plan`
→ submit → approve → build → run is 5 steps for what should be 1, and
the backend will reject the first call anyway.

## Quick decision table

| User ask | Right path |
|---|---|
| "create a workflow that pings X" | `fw_workflows:create_workflow` (draft), then `run_workflow` |
| "build me a backup pipeline across 3 devices" | `fw_workflows:create_workflow` (draft) |
| "I need approval before this lands" | `create_workflow_plan` → submit → approve → build |
| "prepare a plan for the network team to review" | `create_workflow_plan` |
| "promote qa-workflow-X to production" | `fw_workflows:promote_workflow` (no plan needed; env gates already enforce this) |

## Plan lifecycle (when you do use one)

```
draft → submitted → approved / rejected → built
```

- `create_workflow_plan` (dedicated tool) — draft.
- `fw_plans:submit_plan(id)` — operator can do this.
- `fw_plans:approve_plan(id, {approved_by})` — **admin only**; requires
  naming the approver (e.g. `approved_by: "alice"` or the user's id).
  Without this field the backend returns 400. The DTO accepts only
  `approved_by` — extra properties like `comment` are silently dropped.
- `fw_plans:reject_plan(id, {approved_by, reason})` — admin only. The
  reviewer field is `approved_by` (reused DTO field name, same as
  approve) plus a required `reason` string.
- `fw_plans:build_plan(id)` — admin only; creates the real workflow
  row(s) tied to `plan.workflow_id`.

If the caller isn't admin, `approve_plan` / `build_plan` returns 403.
Tell the user that specifically — don't invent a workaround. Never
attempt to bypass governance.

## Audit trail

Every mutation emits an audit event (before + after snapshots). Read it
from the `fw_admin_readonly` spec:

- `fw_admin_readonly:list_audit_events` — filter by `entity_type`,
  `action`, `user_id`, `from`, `to`.

Use for "who changed X" or "when did Y get promoted" questions.
Cross-reference with `fw_admin_readonly:list_traces` for the full
request-level timeline (join on `request_id`).

## Metrics

- `fw_admin_readonly:runs_metrics(days=N)` — runs by day + top failing
  workflows.
- `fw_admin_readonly:auth_metrics(days=N)` — sign-in activity.

Render these as markdown tables.

## Key reminders

- **Admin endpoints are admin-only.** Don't try to escalate.
- **Creating workflows and running them is NOT admin-only.** Operator
  role can create, edit, run, promote (within env gates). Viewer can
  read. Don't refuse an operator's "run this workflow" ask as if it
  needed admin.
- **Production workflows are immutable** — that's enforced by the
  backend (409), not by governance. Don't conflate "immutable" with
  "needs approval".
