# Skill: troubleshooting

A quick reference for the most common problems users bring.

## "my workflow run failed"

1. `get_run_details(run_id)` — read `failed_count` and the step list.
2. Pick the first failed step → `get_step_logs(step_run_id)`.
3. Match the error against the table below and propose the fix.

### Common step failures

| Error snippet | Likely cause | Fix direction |
|---|---|---|
| `input.script is required` / `snippet code is empty` | Python/Ansible node pointing at a snippet with no `Code` | Edit snippet in `/snippets/{id}`, paste code |
| `input.host is required` / `cannot resolve ping host` | Ping node with no host and no device targets | Either add target_devices at run time or hardcode input.host |
| `SSH connection failed` / `auth failed` | Bad or expired credential on the device | Rotate in `/credentials` |
| `request timed out after Ns` | Upstream hung (REST call, Python, Ansible) | Bump `timeout_seconds` on the service OR fix the upstream |
| `401 Unauthorized` (rest_call / integration_action) | Missing/expired secret | `/admin/secrets` — rotate or create the reference |
| `operation 'X' not found` | Spec removed or renamed | Re-check via `discover_operations` |
| `permission denied: tool X requires higher role` | Viewer trying a write op | User's role is too low — they need operator/admin |
| `Blocked: script imports disallowed module 'Y'` | Python sandbox rejection | Rewrite script using allowlisted modules (json, math, datetime, re, ipaddress, flowweaver_runtime) |
| step succeeded but `output_payload = { "raw": "" }` and logs show `exit=0` with empty stdout/stderr | Python snippet defined `def run(ctx):` but never called `set_output()`. Usually caused by a stray bare identifier at the end (e.g. `lldp_collect_and_sync`). | Remove the dangling identifier; confirm `run(ctx)` actually calls `set_output(...)`. The runtime auto-invokes `run(ctx)` on exit — no trailing `run(None)` is needed. |
| Tracebacks on several python steps of the same run (`KeyError`, `IntegrationError`, …) | `run(ctx)` raised in one step and the downstream steps failed reading its output. | Fix the first traceback in time, not the last; the rest are the cascade. |
| `IntegrationError: integration 'X' not available — known: <none>` (from a `python_snippet` step) | The workflow node's `config_overrides` has `"<X>_integration_id": "<value>"` but `<value>` is not a valid UUID AND does not match any active integration's name. Check `worker.python.integration_not_found` in the worker logs for the exact `handle`/`value` pair that was dropped. | Fix the value: copy the integration's UUID from `fw_integrations:list_integrations` into `config_overrides`. A display name (e.g. `"netbox"`) is tolerated as a fallback but only when there's exactly one active integration with that name — prefer the UUID. |
| Report (PDF/HTML/CSV) is generated successfully but literal `{{ steps.X.output.Y }}` text shows up inside cells, callouts, or stats | The report node is `target_mode=once` and the upstream producer was `target_mode=per_device`. Once-mode downstream nodes see the aggregated envelope `{ "devices": [...] }`, so paths like `output.device` / `output.updated[0]` resolve to nothing and the resolver leaves the literal in place (by design — unresolved templates are not errors). | Rewrite the templates with `devices[N]` indexing (e.g. `{{ steps.X.output.devices[0].device }}`), OR flip the report to `target_mode=per_device` for one file per device, OR insert a `transform` (JMESPath) node to flatten the envelope first. See `workflows.md` and `reports.md` for the three patterns. |
| `ansible-playbook is not installed` | Worker image missing ansible | Ops issue — rebuild worker with ansible package |
| `bwrap: No permissions to create new namespace` **or** `python sandbox could not create a namespace after retries` | Python sandbox (bwrap) can't create namespaces. **A host or worker problem, NOT the workflow** — never loop editing the DAG over this. | Ask an admin to follow `docs/ops/python-sandbox-host-requirements.md` (`sudo ./deploy/setup-host.sh` applies the fix). Hints: intermittent across runs → a container without the sandbox relaxations claimed the job (only the `worker` service should carry the `sandbox` tag); some steps of the same run fail → lower `Python:MaxConcurrentSandboxes` or the snippet's `MaxParallel`; message says `Resource temporarily unavailable` → raise `Python:MaxProcesses`. |

## "the agent keeps saying 'thinking…' forever"

Each chat turn records an `ai.chat.run` trace with a wall-clock deadline
(`AiChat:StreamDeadlineSeconds`, default 240 s; env
`AI_STREAM_DEADLINE_SECONDS` in compose). When it fires, the trace closes
with status `timeout` and the chat shows the partial answer. A trace still
`started` well past the deadline means the backend stalled or restarted
mid-turn — ask an admin to check the backend logs.

## "a workflow won't save"

- `409 production_immutable`: user is trying to edit a production
  workflow. Clone to draft first.
- `400 validation failed`: the DAG doesn't match the schema. Read the
  error detail and fix the specific field.
- `403 permission denied`: user role is insufficient.

## "an integration action keeps timing out"

1. `fw_integrations:health_integration(id)` — is the base endpoint up?
2. `fw_admin_readonly:list_traces` filtered by `action=tool.call.execute_operation`
   to see the history of recent attempts + their durations.
3. If health is green but calls are slow, bump the integration's timeout.

## Escalation

When you've genuinely hit a wall, say so. Example:

> I've hit a wall — `get_step_logs` returns the same permission-denied
> no matter which credential I reference. The credential row exists but
> the decryption key may have rotated out of the key-ring. This is an
> infra-level issue; ping whoever manages the `DataProtection` key-ring.
> Meanwhile, a temporary workaround is X.

Always name the escalation path. Never just say "it's broken".
