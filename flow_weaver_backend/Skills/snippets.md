# Skill: snippets & integrations

**Snippets** describe what a workflow node *does*. **Integrations**
describe how to talk to an external REST system. Both are the building
blocks every workflow node references via `snippet_id`.

## Snippet types (built-in handlers)

| Type | What it does | Code lives in |
|---|---|---|
| `ping` | ICMP echo | input.host or device.IpAddress fallback |
| `rest_call` | HTTP call to arbitrary URL | input.url / method / body |
| `ssh` | SSH command on one device (seeded on first boot) | `config_overrides.command`; host/credential fall back to the target Device. See `ssh.md` skill for the full shape. |
| `python_snippet` | Run a Python script | `snippet.Code` — edited in `/snippets/{id}` |
| `ansible_playbook` | Run an Ansible playbook | `snippet.Code` (YAML) |
| `transform` / `jmespath` | JSON transform via JMESPath. **Pure projection only — no ternary, no conditional logic.** See "Transform handler limits" below. | `snippet.Code` (expression) |
| `integration_action` | Call a pre-registered Integration action | `integration_id` + `action_id` |
| `report` | Generate PDF/HTML/CSV/XLSX and persist it in `/admin/artifacts` | `config_overrides.format` + `config_overrides.document` |
| `netconf`, `snmp_v3` | **stubs** — return a clear error |

For `python_snippet`: the script body lives on the snippet row (not the
node). Workflow nodes reference the snippet; the handler pulls the code
automatically. When authoring or editing the script body, follow the
rules in `python_snippets.md` — in particular, scripts call external
APIs via `integration("<name>")` (never raw `urllib`/`requests`), and
the snippet's `config_overrides` must declare the integrations as
`<name>_integration_id` so the worker can pre-resolve credentials.

## Whether a step CHANGES anything — and when omitting it fails the step

Separate from idempotency, and often confused with it. Idempotency says whether
an action could be **undone**; this says whether anything **was done**. A `GET`
that returned 200 and a `DELETE` that returned 200 are both a successful step.

The run's `final_state`, its rollback plan and its audit trail are all built
from this, so it is a measurement, never a default.

**Most handlers measure it themselves** and you do nothing: `rest_call` and
`integration_action` read the HTTP verb, `git` reads the commit sha,
`ansible_playbook` reads the `changed=` count in the play recap, `ping` and
`transform` change nothing by construction, `email_send` / `report` /
`slack_message` change something whenever they succeed.

**Three types cannot know, because you supply the action**: `ssh`,
`python_snippet` and `mcp_call`. For those you declare, and *where* depends on
where the action lives:

| The action lives in | Declare on | How |
|---|---|---|
| the node (`ssh` commands, an mcp tool name) | the node | `config_overrides: { "changes": true \| false }` |
| the snippet's own code (a python script) | the snippet | `changes_state: true \| false` on create/update |

The node wins over the snippet, being more specific — the same `ssh` snippet
runs `show version` on one node and `configure terminal` on another.

**A step of one of those types with no declaration anywhere FAILS**, with
`error_code: change_undeclared` naming the snippet and the field to set. That
is deliberate: the alternative is a guess, and every run outcome downstream
would inherit it.

Only a literal `true` / `false` counts. A string `"true"` is not a declaration
and the step still fails.

When you create a `python_snippet`, set `changes_state`. When you wire an `ssh`
or `mcp_call` node, set `config_overrides.changes`. A read-only `ssh` node that
polls `show version` is `false`; one that pushes config is `true`.

Never declare `false` to make a run look clean — `false` keeps the node out of
the rollback plan, and that plan is what someone reads after a failure.

## Choosing a node type (decision ladder)

Pick the MOST specific node that fits, in this order. `python_snippet` is
the **last resort**, not the default.

1. **Sending email** → the native `email_send` snippet (SMTP channels an
   admin configures under `/email`; omit `channel_id` for the default
   one — no integration/action GUIDs needed). **Posting to Slack** →
   `slack_message`. Never model these as `integration_action` or
   `python_snippet` — see `Skills/email.md` / `Skills/slack_integration.md`.
2. **One call to an external REST system** (NetBox, AWX, Infoblox,
   ServiceNow, Jira, …) → `integration_action`, referencing a
   registered Integration + action. **Check first** with
   `fw_integrations:list_integrations` / `list_palette_actions`; if a
   matching integration + action exists, use it. It's more governable
   (declarative, auditable) and no less capable than a script for a single
   call.
3. **CLI on a device** → `ssh`. **ICMP** → `ping`. **Ansible** →
   `ansible_playbook`. **PDF/HTML/CSV/XLSX artifact** → `report`.
4. **Pure JSON reshape, no conditionals** → `transform` (JMESPath).
5. **Custom logic the above can't express** — multi-call orchestration,
   loops, diffs, conditional output, vendor parsing → `python_snippet`.
   Even here, external calls go through `integration("<name>")` (the sandbox
   blocks `requests`/`urllib`), so you still consume the registered
   integration — you never hand-roll HTTP.

**No Integration registered for the system the step needs?** Do NOT silently
fall back to `python_snippet`. Tell the user and offer
`fw_integrations:create_integration_bundle` (paste base_url + auth, or an
OpenAPI spec). `python_snippet` is for genuine custom logic, never a
workaround for a missing integration.

A single REST GET/POST written as a hand-rolled `python_snippet` is the
anti-pattern we're correcting.

## Transform handler limits (HARD RULES)

The `transform` handler runs JMESPath via `DevLab.JmesPath`. JMESPath is
**pure projection**, not a programming language. Generating any of the
syntax below makes the runner reject the snippet with
`Error(1, NN): syntax, near '?'` (or similar) before the workflow can
even execute.

**Forbidden in JMESPath — these are NOT in the spec:**

| Forbidden syntax | Why | Use instead |
|---|---|---|
| Ternary `cond ? 'A' : 'B'` | Not in JMESPath; jq/JS only. | `python_snippet` (conditional logic). |
| `if(cond, a, b)` | Not in DevLab. | `python_snippet`. |
| `&&` / `\|\|` returning anything other than booleans in a conditional | Lazy-evaluation idioms differ. | Return booleans + values separately, branch in Jinja or Python. |
| Computed keys `{(expr): value}` | Not in JMESPath. | `python_snippet`. |
| Multiline expressions | Single expression only. | Split into chained transforms or use `python_snippet`. |

**Allowed and idiomatic:**

| Pattern | Example |
|---|---|
| Filter list | `results[?ok == \`true\`]` |
| Project with new keys | `{name: device.name, ip: device.ip_address}` |
| Function call | `length(results[?ok])`, `sort_by(...)`, `to_string(...)` |
| Boolean | `output.exit_code == \`0\` && length(results) > \`0\`` |
| Default-when-null | `(output.stderr || 'no error')` (only for fallback values) |

**Decision rule for the agent**: any time the transform output depends
on a condition (status `Success`/`Failure`, including/excluding fields,
choosing between two values), **use a `python_snippet` instead**.
JMESPath is for projecting/reshaping data whose shape is already known.

Concrete example — wrong vs right:

```
# WRONG — runtime error, JMESPath has no ternary
{ status: ok ? 'Success' : 'Failure' }

# RIGHT — return both flag and candidate values; branch in Jinja or Python downstream
{ ok: ok, success_message: 'parsed ok', failure_message: stderr }

# BEST — use python_snippet for any conditional output
ok = output['exit_code'] == 0
set_output({'status': 'Success' if ok else 'Failure', ...})
```

## API — `fw_snippets`

- `fw_snippets:list_snippets` — browse / search by type.
- `fw_snippets:get_snippet` — details, including `Code`.
- `fw_snippets:create_snippet` — new snippet.
- `fw_snippets:update_snippet` — edit `Code`, timeouts, `target_mode`.
- `fw_snippets:delete_snippet` — soft-delete.

`target_mode` is either:

- `per_device`: one step_run per target device (the handler loops).
- `once`: one step_run regardless of targets. Use for scripts or
  transforms that don't need a device context.

## API — `fw_integrations`

Integrations are external REST systems (AWX, NetBox, Infoblox, ServiceNow).
Each integration has a `base_url` + auth config; each `integration_action`
is a specific endpoint (e.g. `AWX:launch_job_template`).

An integration can also own **skills** (prompt fragments) and **specs**
(OpenAPI YAML). When a skill or spec is scoped to an integration, the
agent knows which base URL and credentials to use for the operations it
mentions — `${secret:integration:<name>:<field>}` resolves against the
integration's `auth_config`.

Flow when the user asks "call AWX's launch_job":

1. `fw_integrations:list_integrations` to show which ones exist.
2. `fw_integrations:get_integration` for base_url + status.
3. `fw_integrations:list_actions` — returns **all actions**.
   There's no filter-by-integration_id query param, so if the user only
   wants actions for one integration, fetch the full list and filter
   client-side by `integration_id` in the response. Alternatively, use
   `fw_integrations:list_palette_actions` for the pre-joined flat list.
4. Use in a workflow node via the seeded `integration_action` snippet:
   call `fw_snippets:list_snippets`, find the row with
   `type = "integration_action"`, and put **its UUID** in `snippet_id`.
   The string `"integration_action"` is **not** a valid literal here —
   the executor rejects it with `invalid snippet_id`. Then set
   `config_overrides = { integration_id, action_id, body, params, query }`.

Integrations are an orthogonal path to the generic `execute_operation`
tool: use `fw_integrations` when the user wants to **wire an integration
into a workflow** (persistent), and use `execute_operation` when they
want to **ask a one-shot question against any REST API in the catalog**.

### Bundled create

`fw_integrations:create_integration_bundle` is the canonical way to set
up a new integration from the chat: it takes `{ integration, skills,
specs }` and creates everything in a single transaction. If the user
pastes an OpenAPI YAML plus a short prompt snippet, combine both into
one bundle call — partial failures roll back cleanly.

## Health checks

`fw_integrations:health_integration(id)` pings the integration's health
endpoint and returns `{status, last_checked_at, status_code}`. Use this
when the user reports "my AWX actions aren't working" — check health
first.
