# Skill: workflow triggers

Triggers bind a workflow to an event or schedule so it runs without a
human pressing "Run". Each trigger carries:

- `workflow_id` — the target.
- `name` — required.
- `type` — `cron` | `schedule` | `webhook` | `event`.
  `cron` and `schedule` are synonyms; both are time-based and require
  `cron_expression`. **The DTO field is `type`, not `kind`.**
- `cron_expression` — standard 5-field cron; interpreted in `timezone`.
- `timezone` — IANA TZ name (default UTC).
- `enabled` — on/off without deleting.
- `input_schema`, `input_defaults` — payload shape + defaults for runs
  this trigger fires.
- `route` — for `webhook` triggers, the path suffix clients POST to.
- `notification_webhook_url`, `notify_on: [success|failure|timeout]` —
  optional post-run notifications.
- Runtime state (read-only): `last_run_at`, `last_run_status`, `next_run_at`.

## Creation ≠ list/update/delete route

Real API gotcha:

- **Create** → `POST /workflow/{workflowId}/triggers`
  (`fw_workflows:create_workflow_trigger`)
- **List / get / update / delete** → `/workflowTrigger[/…]`
  (`fw_triggers:list_triggers`, `fw_triggers:get_trigger`,
  `fw_triggers:update_trigger`, `fw_triggers:delete_trigger`)

`POST /workflowTrigger` (no workflow id in the path) returns **400**
with a message redirecting you to the correct route. Don't call it.

## Common user asks

- **"schedule this workflow for every 15 min"** → create a trigger with
  `type: "cron"` + `cron_expression: "*/15 * * * *"`. Show the expression
  in the plan so the user can sanity-check it.
- **"stop the nightly backup trigger"** → `update_trigger(enabled: false)`.
  Don't delete unless the user explicitly asked.
- **"why didn't the cron fire last night?"** → read `last_run_at` +
  `last_run_status`. If `last_run_status` is `failed`, pivot to
  `get_run_details` with the last run id.

## Cron expression tips

Standard 5-field cron (minute, hour, day-of-month, month, day-of-week).
If the user says "every Tuesday at 3pm in EU", clarify timezone and set
the `timezone` field — the engine interprets cron in the trigger's
timezone, defaulting to UTC when absent.

Always show the next expected run after creating a trigger, e.g.:

> **Plan**
> Create a cron trigger on workflow `daily-backup`:
> `name: "nightly-backup"`, `type: "cron"`,
> `cron_expression: "0 2 * * *"`, `timezone: "UTC"`.
> POST /workflow/{id}/triggers.
>
> Next run: **2026-04-21 02:00 UTC**.
>
> Confirm with **"yes"**.
