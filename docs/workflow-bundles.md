# Sharing workflows between instances

A workflow is only useful to someone else if it arrives intact. This is the
format that makes that work, and the rules it follows.

## Use `bundle`, not yaml/json

| Format | Round-trips on the same instance | Survives crossing instances |
|---|---|---|
| `bundle` | yes | **yes** |
| `yaml`, `json` | yes | no |

`yaml` and `json` serialize the workflow's nodes verbatim, and a node refers to
its snippet and integration by **GUID**. GUIDs are per-instance. On any other
installation they resolve to nothing, every dependency reads as missing, and the
import has nothing to bind to.

```
GET  /api/workflow/{id}/export?format=bundle    →  <name>.bundle.json
POST /api/workflow/import?format=json          ←  detected automatically
```

In the UI: **Export → Bundle (share / portable)**.

## What travels

```jsonc
{
  "schema_version": "v3",
  "kind": "flow_weaver.workflow_bundle",   // recognised by this marker
  "exported_at": "2026-08-27T12:00:00Z",
  "exported_by": { "product": "flow-weaver", "version": "1.4.2" },
  "requires": {                             // what the receiving engine must support
    "snippet_types": ["ssh", "transform"],
    "capabilities": ["per_device_scope", "subflow", "template_filters"],
    "secrets": [ { "ref": "${secret:store:netops-ssh:password}", "used_by": ["show-version"] } ]
  },
  "workflow": { "name": "...", "description": "...", "environment": "qa",
                "input_schema": {}, "metadata": {} },
  "nodes": [ /* the portable NAME in place of the local id */ ],
  "edges": [ /* verbatim */ ],
  "dependencies": {
    "snippets":     [ { "id", "slug", "name", "type", "code", /* full definition */ } ],
    "integrations": [ { "id", "slug", "name", "type", "base_url",
                        "actions": [ { "id", "name", "method", "path" } ] } ],
    "mcp_servers":  [ { "id", "name", "transport" } ],
    "credentials":  [ { "id", "name", "type", "auth_method", "username" } ],
    "repositories": [ { "id", "name", "remote_url" } ],
    "workflows":    [ { "id", "name", "nodes", "edges", ... } ]   // sub-workflows, flattened
  },
  "triggers": [ { "name", "type", "cron_expression", "route", ... } ]
}
```

Nodes travel with the portable NAME **in place of** the local id, so a node
means something on an instance that has never seen those GUIDs — and so the
node hash is the same wherever the workflow was exported from. A GUID is
meaningful only on the instance that minted it; carrying one would make the
fingerprint instance-specific and a bundle that crossed instances could never
come back equal to itself. `snippet_id` and `subflow_workflow_id` stay GUIDs:
they are structural and are always remapped against definitions the bundle
carries. Importers still accept the legacy id keys (v2
bundles and hand-authored files carry them) and the canonical key wins when
both are present. `dependencies` gives each reference a definition or an
identity; the importer resolves both locally and rewrites the graph. Identity
is translated in exactly one place.

Only the actions the workflow actually uses are included — shipping the whole
catalogue would bloat the file and expose the shape of systems the recipient has
no business seeing.

### `requires`: refuse precisely, or degrade out loud

A bundle that cannot run here must be refused **at import**, naming what is
missing, rather than importing cleanly and failing on its first run against a
real device. So the bundle declares what it needs, and the declaration is
*derived from the graph* — never asserted — so it cannot drift from it.

| capability | declared because |
|---|---|
| `subflow` | a node runs another workflow |
| `template_filters` | a template pipes through a filter (`{{ x \| upper }}`) |
| `run_namespace` | a template reads `{{ run.* }}` |
| `per_device_scope` | a `per_device` step reads another `per_device` step's output by field |
| `max_parallel` | a snippet fans out with `max_parallel > 1` |
| `per_pool` | a snippet targets `per_pool` |
| `conditional_edges` | an edge is `conditional` |
| `python_network` | a python snippet had `network_enabled` at the source |
| `triggers` | the bundle carries triggers |

This installation implements every capability in the table. The vocabulary
also defines `rest_catalog` (a `rest_call` step in the catalogued
`source` / `operation_id` form), which this installation does not implement.
A bundle that requires `rest_catalog`, or any name outside the vocabulary, is
refused (`bundle_capability_unsupported`): a build that does not know what a
name means cannot decide it is harmless. For a v2 bundle the requirement is
inferred from the nodes, so a catalogued `rest_call` step is refused the same
way.

`requires.secrets` lists every `${secret:<source>:<name>:<field>}` marker found
in the graph or in a carried snippet — the reference, never a value. Markers are
copied through untouched (they are not templates; the resolver never touches
them) and the import notes each one so the secret can be created before the
first run. At run time the markers are resolved in `python_snippet` payloads,
in `ssh` inline credentials (`username`, `password`, `private_key`,
`key_passphrase`) and `enable_secret`, and in `rest_call` `url` and `headers`.
A marker anywhere else reaches the handler as literal text. Plain-text values
in `ssh` inline credential fields are stripped on export and refused on
import; only a `${secret:…}` reference is accepted there.

### Portable reference keys

A GUID is meaningless on another installation, so every reference that leaves
the graph is written by NAME:

| referenced | portable key | value |
|---|---|---|
| integration | `integration` | the integration's **slug** |
| integration action | `action` | the action's name, within that integration |
| MCP server | `server` | the server's name |
| credential | `credential` | the credential's name |
| git repository | `repository` | the repository's name |

Export writes the name key in place of the local id key (`integration_id`,
`action_id`, `mcp_server_id`, `credential_id`, `repository_id`); import uses the
name, rewrites it to this instance's id, and
**drops the name**, so the stored graph keeps exactly one source of truth and a
later export re-derives the name from what the node points at *now* rather than
from a label that has since gone stale. A name that matches nothing here is
refused (`bundle_reference_untranslatable`) naming the node and the key.

### Sub-workflows

Every workflow reachable through `subflow` nodes travels, transitively, once
each, in `dependencies.workflows` — with its snippets and references merged into
the same `dependencies` and `requires`, so the subflow nodes resolve on the
receiving instance.

On import they are created **first**, callees before callers (so each caller's
`subflow_workflow_id` can be rewritten to a row that already exists), as drafts
tagged `metadata.is_subflow = true`. An existing local workflow is reused only
on an exact name match **and** identical canonical nodes+edges — anything looser
would bind a caller to a workflow that merely shares a name. A cycle is refused
(`bundle_subflow_cycle`) on export and on read: a workflow that reaches itself
can never run.

### Triggers

A workflow's `cron` and `webhook` triggers travel — without their signing
secret, without their target devices, without their run statistics. On import
each one is created **disabled**, with a **fresh** secret and no targets, and the
notes say what to set before enabling it. Enabling a trigger is an explicit local
act: a file must never be able to start firing a workflow on someone else's
instance. A `route` is kept when it is free here and regenerated (with a note)
when it is taken.

## What does not travel

**Credentials.** An integration is described by slug, name, type and base URL —
never its auth config or headers. The receiving instance authenticates with its
own credentials. This is also why a missing integration is a hard error instead
of something the import creates for you: it would have no way to authenticate.

**`network_enabled`.** It lifts the python sandbox's network isolation and is
admin-gated locally, so a file from elsewhere must not be able to grant itself
that. The snippet is created without it and the import reports that it was
dropped, so an admin can review the code and re-enable it deliberately.

**`verified`.** An imported snippet is unverified here regardless of its status
on the source instance — this installation has not run it.

## Identity: the slug

`Integration` and `Snippet` carry a `Slug`, derived from the name **once** at
creation and never rewritten. Renaming "NetBox" to "NetBox (prod)" therefore does
not break bundles already shared — the slug is the identity, the name is a label.

Resolution order on import:

1. **by slug** — the normal path;
2. **by exact name**, case-insensitive — covers bundles from an instance that
   predates slugs, and systems registered independently on both sides. The
   import reports when it had to fall back to this, so you can confirm it is the
   same system.

Never fuzzy. A near-miss would silently point a workflow at the wrong system.

Existing rows are backfilled at boot (`SlugBackfillService`): idempotent, and
where two rows share a name the **oldest** keeps the clean slug, since anyone who
already shared a bundle meant that one.

## When something is missing

The import **stops and names it**:

```
this instance is missing dependencies the workflow needs:
integration 'NetBox' (slug 'netbox', type 'generic_rest') — source base_url https://netbox.corp;
action 'list_devices' on integration 'ServiceNow'.
Create them here (with their own credentials) and import again.
```

A missing **action** on an integration that does exist almost always means the
target's OpenAPI spec is older — re-upload it on the integration and retry.

Snippets are the exception, and not an inconsistency: their **full definition
travels**, so recreating one is lossless; nothing is invented.

## Idempotency

Importing the same bundle twice does not clone anything. Snippets already present
(by slug, then name) are reused; only genuinely new ones are created. A snippet
created from a bundle keeps the source slug when it is free, so both instances
converge on the same identity and a later bundle from either side resolves by
slug rather than by name.

The workflow itself always lands as a **fresh draft** — a bundle can never place
a workflow straight into production on someone else's instance.

## Version handling

| version | written | read |
|---|---|---|
| `v3` | yes | yes |
| `v2` | no | yes — `requires` is inferred from what is visible, and there are no triggers |

A bundle whose `schema_version` this build does not know is **refused**, naming
the version. Reading a subset of an unknown format would import something
half-wired, which is worse than a clear error. Members this build does not know
are ignored at every other level, so a newer writer's extras are not fatal.

A bundle that references a snippet whose definition it does not carry is refused
for the same reason — completing it by guessing is the behaviour this format
exists to remove.

## Other format identifiers and key aliases

Besides `flow_weaver.workflow_bundle`, older format identifiers are also
accepted in `kind`. `exported_by` says which product wrote the file and is
informational only — nothing dispatches on it; the `schema_version` and the
declared `requires` are what decide.

Some payload keys have an accepted alternative spelling. The canonical key is
the one this product's handlers read; the alias is rewritten to it before the
handler runs (`Services/Worker/PayloadAliases.cs`, plus a few handlers that
resolve inventory names themselves):

| type | canonical | alias |
|---|---|---|
| `transform` | `expression` | `mapping` (an object; it *is* a JMESPath multiselect hash) |
| `integration_action` | `params`, `query` | `path_params`, `query_params` |
| `mcp_call` | `tool` | `tool_name` |
| `ssh` | `use_structured`, `enable_secret` | `structured`, `enable` |
| `ssh` | `commands` | `command` (dropped when `commands` is present) |
| `ansible_playbook` | `hosts` | `device`, `targets`, `host` |
| `report` | `document` | `content` (markdown) |
| `ping` | `host` | `device` (an inventory device **name**, used only when `host` is blank) |
| `ssh` | `device` (an inventory device **name**, resolved here to its address and credential) | `host` (a literal address; ignored when `device` is present) |
| `git` | `repository` | the repository's **name**, resolved here to its id |

`slack_message` accepts a `via` key and ignores it: this product posts with the
deployment token.

An alias is used only when the canonical key is absent — in a node carrying
both, the alias is a leftover key and the canonical one is what it means.
Keys no alias covers are left alone (the handler ignores what it does not read)
but **noted per node at import**, so a typo or a product-specific extension is
visible before the first run rather than after it.

Two forms exist under `rest_call`: the raw one (`url`, `method`, `headers`,
`body`, `query`) that this product runs, and a catalogued one (`source`,
`operation_id`) that resolves an operation out of a stored API spec. This
product has no such catalogue for workflow steps — its equivalent is an
`integration_action` node — so a catalogued step is noted at import and fails
with `not_supported` naming the keys, rather than reporting that `url` is
missing, which is true and useless.

Retry policies are read in either shape. The canonical one is
`{max_retries, initial_delay_seconds, backoff, max_delay_seconds}`; the legacy
`{max_attempts, delay_seconds, backoff}` is translated on import — `max_attempts`
counts the first try and `max_retries` does not, so reading the number across
unchanged would grant one extra attempt on every imported step. Exports are
always canonical.
