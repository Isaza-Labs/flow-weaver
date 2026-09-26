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
| step succeeded but `output_payload = { "raw": "" }` and logs show `exit=0` with empty stdout/stderr | Python snippet defined `def run(ctx):` but never called `set_output()`. Usually caused by a stray bare identifier at the end (e.g. `lldp_collect_and_sync`) or an old snippet written before the runtime's auto-invoke was added. | Remove the dangling identifier; confirm `run(ctx)` actually calls `set_output(...)`. The runtime auto-invokes `run(ctx)` on exit — no trailing `run(None)` is needed. |
| `{ "raw": "" }` on a python step whose Logs show a traceback (`KeyError`, `IntegrationError`, …) and, downstream, every later python step ALSO ends with `{ "raw": "" }` | `run(ctx)` raised. On a worker image older than 2026-09-25 the runtime let CPython swallow the exception (`Exception ignored in atexit callback`, exit 0), so the step was recorded as a success with an empty output, and each downstream snippet reading a key from it raised in turn, hidden the same way. The **first** step whose Logs carry a traceback is the real failure; the rest are the cascade. | Fix the first traceback, not the last. Then rebuild the worker image: since 2026-09-25 a raising `run(ctx)` exits non-zero and the step fails with the traceback in `Error`, and the handler refuses to mark an "ignored atexit" exit as success. |
| `IntegrationError: integration 'X' not available — known: <none>` (from a `python_snippet` step) | The workflow node's `config_overrides` has `"<X>_integration_id": "<value>"` but `<value>` is not a valid UUID AND does not match any active integration's name. Check `worker.python.integration_not_found` in the worker logs for the exact `handle`/`value` pair that was dropped. | Fix the value: copy the integration's UUID from `fw_integrations:list_integrations` into `config_overrides`. A display name (e.g. `"netbox"`) is tolerated as a fallback but only when there's exactly one active integration with that name — prefer the UUID. |
| Report (PDF/HTML/CSV) is generated successfully but literal `{{ steps.X.output.Y }}` text shows up inside cells, callouts, or stats | The report node is `target_mode=once` and the upstream producer was `target_mode=per_device`. Once-mode downstream nodes see the aggregated envelope `{ "devices": [...] }`, so paths like `output.device` / `output.updated[0]` resolve to nothing and the resolver leaves the literal in place (by design — unresolved templates are not errors). | Rewrite the templates with `devices[N]` indexing (e.g. `{{ steps.X.output.devices[0].device }}`), OR flip the report to `target_mode=per_device` for one file per device, OR insert a `transform` (JMESPath) node to flatten the envelope first. See `workflows.md` and `reports.md` for the three patterns. |
| `ansible-playbook is not installed` | Worker image missing ansible | Ops issue — rebuild worker with ansible package |
| `bwrap: No permissions to create new namespace` **or** `python sandbox could not create a namespace after retries` | Python sandbox (bwrap, `--unshare-all`) can't create namespaces. **Infra, NOT the workflow** — never loop editing the DAG over this. The worker already caps concurrent sandbox launches (`Python:MaxConcurrentSandboxes`) and auto-retries transient setup failures with backoff, so the second (wrapped) message only appears once those retries were exhausted. First tell the sub-cases apart: did OTHER `python_snippet`/`transform` steps in the SAME run succeed? | **FIRST — intermittent across runs (some runs fine, others fail)** → a container WITHOUT the seccomp/apparmor relaxations claimed the job. The error names the failing container (`container '<hostname>'`); compare against `docker ps`. Python steps are routed to the `sandbox` queue tag that only the relaxed `worker` service should claim — check the `Worker__Tags` overrides in `deploy/docker-compose.yml` (backend: `default`+`orchestrator` only; worker: also `sandbox`). **ALL python/transform steps fail** → run `docker exec -u app <worker> bwrap --ro-bind / / --unshare-user true`. **Fails in the container but the host has `kernel.unprivileged_userns_clone=1`** → it's the **Docker default seccomp profile** blocking the non-root worker (the common containerized case): add `security_opt: [seccomp=unconfined, apparmor=unconfined]` (or `cap_add: [SYS_ADMIN]`) to the worker service. **Fails on the HOST too** → unprivileged userns is off at the kernel: enable it (Debian/Ubuntu `sysctl kernel.unprivileged_userns_clone=1`; RHEL `sysctl user.max_user_namespaces=15000`). **SOME succeed, others fail** → userns works; the namespace table is being exhausted under the per-device fan-out (and/or zombie processes leak namespaces). Raise `sysctl user.max_user_namespaces=64000`, and/or **lower `Python:MaxConcurrentSandboxes`** (process-wide cap on concurrent bwrap launches) and/or the snippet's **`MaxParallel`** (per-snippet concurrency — now enforced by the worker, so lowering it does reduce concurrent bwrap of that snippet). Check `ps` for unreaped children if it persists. **If the message is `Creating new namespace failed: Resource temporarily unavailable` (EAGAIN), NOT `No permissions`** → it's a process/thread limit (RLIMIT_NPROC), not seccomp/userns: the `--nproc` cap counts the multi-threaded worker's own threads, so raise **`Python:MaxProcesses`** and/or lower `Python:MaxConcurrentSandboxes`. |

## "the agent keeps saying 'thinking…' forever"

Check `/admin/traces` filtered by `category=ai` and `status=started`.
If the trace is still open after 25s, the deadline should have fired
— look for a matching `ai.chat.stream.timeout` trace within the same
`request_id`. If there isn't one, the backend probably didn't rebuild;
tell the user to restart the container.

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
