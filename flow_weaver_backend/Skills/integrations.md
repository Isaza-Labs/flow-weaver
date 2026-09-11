# Skill: integrations — prefer `integration_action` over hand-rolled python

An **Integration** is a registered external REST system (NetBox, AWX,
Infoblox, ServiceNow, Jira, an email gateway, Slack, …) with a `base_url`,
auth config, and a catalog of **actions** (`IntegrationAction` rows, often
discovered from an uploaded OpenAPI spec). Wiring one into a workflow node is
the `integration_action` snippet type.

## The default rule

When a workflow step calls a known external system, the node is an
`integration_action` — **not** a `python_snippet`. Reach for `python_snippet`
only when the step needs logic the declarative node can't express
(multi-call orchestration, loops, diffs, conditional output, vendor parsing).
And even then, the script calls the system through `integration("<name>")`
(the sandbox blocks `requests`/`urllib`), so it still routes through the
registered integration's auth + SSRF guard + audit.

Why prefer `integration_action`:

- **Governable & auditable** — the call is declarative (`integration_id` +
  `action_id`), visible in the graph, and gated by the same policy/RBAC.
- **No bespoke code to maintain** — no Python body, no Mermaid diagram, no
  scaffold-code gate to satisfy.
- **Not less capable for a single call** — a lone GET/POST is exactly what
  `integration_action` is for.

A single REST GET/POST written as a hand-rolled `python_snippet` is an
anti-pattern (`base.md` § Anti-patterns, `snippets.md` § decision ladder).

## Check before you build

Before drafting any node that touches an external system:

1. `fw_integrations:list_integrations` — does an integration for this system
   already exist (and is it `Enabled`)?
2. `fw_integrations:list_palette_actions` — the pre-joined flat list of
   `(integration_id, action_id, name)` you put on the node. (Or
   `list_actions` + filter client-side by `integration_id`.)
3. If a matching integration + action exists → use `integration_action`
   (next section).
4. If the system has **no** registered integration → do NOT silently fall
   back to `python_snippet`. Tell the user and offer to create one with
   `create_integration_bundle` (below). Only after the user declines, or for
   genuinely-custom multi-step logic, reach for `python_snippet`.

## Wiring an `integration_action` node

The literal string `"integration_action"` is the seeded snippet's **type**,
not a valid `snippet_id`. Resolve the seeded row's UUID first:

```
fw_snippets:list_snippets(type="integration_action")   → take its UUID
```

Then the node:

```json
{
  "id": "create-ticket",
  "snippet_id": "<integration_action snippet UUID>",
  "config_overrides": {
    "integration_id": "<servicenow integration UUID>",
    "action_id":      "<create_incident action UUID>",
    "body":   { "short_description": "{{ steps.detect.output.summary }}" },
    "params": { "device_id": "{{ input.device_id }}" },
    "query":  { "sysparm_limit": "1" }
  }
}
```

- `integration_id` / `action_id` are UUIDs (preferred — survive renames).
- `body` is the request payload; `params` fills `{path}` placeholders;
  `query` becomes the query string.
- Auth, base URL, TLS and the SSRF guard come from the integration row — you
  never put a token or full URL on the node.

## When no integration is registered — create one

`fw_integrations:create_integration_bundle` is the one-call setup: it takes
`{ integration, skills, specs }` and creates the integration (+ optional
prompt skills + OpenAPI specs, which become the `IntegrationAction` catalog)
in a single transaction. If the user pastes an OpenAPI YAML plus a base_url
and auth, bundle them. Secrets go in via `${secret:integration:<name>:<field>}`
references, never plaintext (see `python_snippets.md` § secrets).

## `integration_action` vs `execute_operation` vs python

| You want… | Use |
|---|---|
| Persist a call **inside a workflow** | `integration_action` node |
| Ask a **one-shot** question against any catalog API right now | `execute_operation` (see `discovery.md`) |
| Multi-step logic across several calls / transforms / conditionals | `python_snippet` calling `integration("<name>")` |
| Generic HTTP to an arbitrary URL with **no** product behind it | `rest_call` |

## Health

`fw_integrations:health_integration(id)` → `{status, last_checked_at,
status_code}`. When the user says "my AWX action isn't working", check health
before assuming the workflow is wrong.

## On import (foreign DSL → FlowWeaver)

The import pipeline maps tasks that name a recognised product
(NetBox/Infoblox/ServiceNow/AWX/email/Slack/…) to `integration_action` and
surfaces existing Integration rows as mapping candidates, instead of
auto-drafting a python body. If the wizard shows a missing `integration_action`
with candidates, map it to the existing integration rather than generating a
script.
