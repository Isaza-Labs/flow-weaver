# workflow.v1 — Snippet type contracts

Status: draft · Contract 1.1.0-draft · Oracle: FlowWeaver (this repository)

A node executes the snippet its `snippet_id` names; the snippet's `type` picks the
handler. Two implementations that agree on the graph but not on what each handler reads
produce the worst possible outcome: a bundle that imports and fails on the first
run. This document fixes, per type, the **canonical input keys**, the **aliases**
an importer/handler must accept, the **portable output** a downstream template may
rely on, and the default idempotency.

Rules that apply to every type:

1. **FlowWeaver is the oracle for payload keys.** Where implementations have used
   different keys for the same thing, the key marked **C** below is canonical and
   the other spelling is an accepted alias. Handlers MUST accept both; exporters
   MUST write the canonical key.
2. **Identity keys are names, not ids** (bundle/SPEC.md §4): `integration`,
   `action`, `server`, `credential`, `repository`. Handlers resolve them locally.
3. **Extra keys are allowed.** A handler ignores keys it does not know — but an
   importer MUST note keys that no known alias covers, per node, so a typo or a
   product-specific extension is visible at import, not at run time.
4. **The input payload is `config_overrides` after template resolution**, merged
   over the snippet's declared defaults (`input_schema.properties.*.default`).
5. **Portable output** is the minimum a template may address. Products may add
   fields; they may not rename or remove the portable ones.
6. Default idempotency is the handler floor; a snippet may declare a stricter tier,
   never a weaker one (execution/SPEC.md §2).

Legend: **C** canonical · *A* alias.

## `ping`
Reachability probe. The probe method (ICMP echo vs. TCP connect) is
implementation-defined; a product whose probe is not an ICMP echo SHOULD name it in
`method`, so a reader can tell which probe produced the numbers. (The oracle sends
ICMP and emits no `method`; requiring the field would make the oracle
non-conformant, so this is a SHOULD.)

| key | notes |
|---|---|
| `host` **C** | IP or inventory device name; falls back to the current `{{ device.ip }}` in `per_device` mode |
| `device` *A* | inventory device name |
| `count` **C** | echo count; ignored by a TCP prober, which makes one attempt whatever it says |
| `timeout_ms` **C** | per probe |
| `port` | extension (TCP prober); default 22 |

Portable output: `{ "success": bool, "rtt_avg_ms": number, "packets_sent": number,
"packets_received": number, "raw_output": string }` — FlowWeaver's field set.
`packet_loss` (number, `(sent - received) / sent`) is derived from that pair and
SHOULD be emitted beside it.

A **TCP prober reports one attempt**: one connection attempt is one packet, so
`packets_sent` is 1 and `packets_received` is 1 or 0; `rtt_avg_ms` is the connect
time — an average over a single sample — and is 0 when nothing answered. The
numbers are therefore the same fields measuring a different thing (a handshake to
one port, not a round trip to the host), which is exactly what `method` is for.
A TCP prober emits `method: "tcp"` and may add, as extensions under rules 3 and 5,
fields such as `host`, `port`, `reachable` and `latency_ms` (null when
unreachable). None of these are portable; a template that must run on any
implementation reads the portable set above.

Whether an unreachable target fails the step is implementation-defined:
FlowWeaver fails it; another implementation may return a successful step so the
graph can branch on the result. `success` carries reachability either way.

Default idempotency: `idempotent`. Target modes: `once`, `per_device`.

## `ssh`
Runs commands on a device.

| key | notes |
|---|---|
| `commands` **C** | array of strings |
| `command` *A* | single string → `[command]` |
| `device` **C** | inventory device name or id. In `per_device` mode defaults to the current device. |
| `host` *A* | IP; the handler resolves it to an inventory device by IP. No match → `not_found`, never an ad-hoc connection. |
| `credential` **C** | credential **name**; overrides the device's inventory credential |
| `credential_id` *A* | translated at import via `dependencies.credentials`; never written by exporters |
| `username` | an identity, not a secret — a plain value travels, like any other identity in this contract |
| `password`, `private_key`, `key_passphrase` | **not portable as plain values** — the importer refuses the node (`bundle_reference_untranslatable`) unless the value is a `${secret:…}` reference, which is preserved verbatim and listed in `requires.secrets`. A handler that receives such a value resolves it at run time, and an implementation that cannot resolve it MUST refuse the node at import rather than accept a marker it will send on the wire. |
| `use_structured` **C** / `structured` *A* | parse output with the device's TextFSM/structured parser |
| `stop_on_error` **C** | default true |
| `enable_secret` **C** / `enable` *A* | enter privileged mode; a `${secret:…}` reference or a credential field |
| `timeout_seconds`, `port`, `device_type`, `setup_commands`, `read_until_pattern`, `direct_exec`, `preserve_ansi`, `use_timing` | FlowWeaver extensions; a handler without them ignores them and the importer notes it |

Portable output: `{ "results": [ { "command": string, "ok": bool, "output": string, "parsed": any|null, "elapsed_ms": number, "error": string|null } ], "stdout": string, "exit_code": number|null }`.
Default idempotency: `non_reversible`. Target modes: `once` (requires `device`/`host`), `per_device`.

## `rest_call`
Two valid forms under one type; the discriminator is which key is present.

**Raw form**: `url` **C**, `method` **C**, `headers` **C**, `body` **C**, `query` **C**.
**Catalogued form**: `source` **C** (spec/integration slug), `operation_id` **C**,
`path_params` **C**, `query_params` **C**, `body` **C**.

A handler MUST implement the raw form. The catalogued form is optional: a bundle
using it declares the `rest_catalog` capability (bundle/SPEC.md §2.2), and an
implementation without it **refuses the bundle at import**. FlowWeaver implements
only the raw form, so it refuses such a bundle, and a catalogued `rest_call` step
that reaches its handler fails with `not_supported`. The declared capability turns
what would otherwise be a bundle that imports clean and fails on its first run into
a refusal the operator can act on.

The raw form is subject to the same outbound URL guard the product applies to
integrations. `${secret:…}` references inside `headers` and `url` are resolved at
run time and listed in `requires.secrets`.

Portable output: `{ "status_code": number, "body": any, "headers": object }`.
Default idempotency: `requires_compensation`.

## `transform`
Reshapes a JSON value with a **JMESPath** expression.

| key | notes |
|---|---|
| `expression` **C** | JMESPath, on the payload or on the snippet's `code` |
| `input` **C** | the value to transform; default: the whole `steps.*` outputs map |
| `language` | must be `jmespath` when present |
| `mapping` *A* | `{ "<out>": "<path>" }` — equivalent to the JMESPath multiselect hash `{ out: path }`; handlers MUST accept it and exporters SHOULD translate it to `expression` |

Portable output: the expression's result, verbatim.
Default idempotency: `idempotent`. Target mode: `once`.

## `integration_action`
Calls an action of a catalogued integration.

| key | notes |
|---|---|
| `integration` **C** | integration slug (name accepted on resolution) |
| `action` **C** | action name |
| `integration_id`, `action_id` *A* | translated at import (bundle/SPEC.md §4) |
| `body` **C** | request body |
| `params` **C** / `path_params` *A* | path parameters; `params.device_id` is the FlowWeaver convention for a device-scoped call |
| `query` **C** / `query_params` *A* | query string |

Portable output: `{ "status_code": number, "body": any, "headers": object }`.
Default idempotency: `requires_compensation`.

## `mcp_call`
| key | notes |
|---|---|
| `server` **C** / `mcp_server_id` *A* | MCP server name |
| `tool` **C** / `tool_name` *A* | tool name. FlowWeaver's workflow builder writes `tool_name`; its handler normalises it to `tool`, and a node carrying both calls the tool `tool` names |
| `arguments` **C** | object |

Portable output: `{ "content": string, "structured": any|null, "is_error": bool }` —
FlowWeaver's field set (`McpCallHandler`).
Default idempotency: `requires_compensation`.

## `python_snippet`
Script on the snippet's `code`; the resolved payload is delivered on stdin as JSON;
stdout JSON is the output. `timeout_seconds` on the snippet. `network_enabled` is
never granted by an import. Module allow-lists are local policy: an importer notes
imports in `code` that its allow-list rejects (`requires` cannot express it).
FlowWeaver's `<name>_integration_id` injection is an extension: portable scripts receive
integrations through explicit `${secret:…}` references or `integration_action`
steps.

Portable output: the script's stdout JSON.
Default idempotency: `requires_compensation`.

## `git`
Superset already compatible. Canonical keys: `operation` (`read_file` | `write_file`
| `commit` | `pull` | `push` | `list_files` | `status` | `diff`), `repository` **C**
(name) / `repository_id` *A*, `path`, `paths`, `ref`, `branch`, `content`,
`commit_message`, `push`, `author_name`, `author_email`. A handler without an
operation fails with `not_implemented`.
Default idempotency: `requires_compensation` (`read_file`, `list_files`, `status`,
`diff`, `pull` may declare `idempotent`).

## `report`
| key | notes |
|---|---|
| `format` **C** | `html` \| `csv` \| `xlsx` \| `pdf` \| `markdown` |
| `document` **C** | structured document `{ title, sections[…] }` (FlowWeaver shape) |
| `content` *A* | markdown string — equivalent to `document: { sections: [ { markdown: content } ] }` |
| `retain_days` | extension |

`markdown`, `html` and `pdf` render the whole document, prose included. `csv` and
`xlsx` are tabular formats and carry **only the document's tables** — the markdown
tables in the body, which is also where `document.stats` and
`document.sections[].tables` land once the structured document is rendered. One
table is one sheet (`xlsx`) or one header row plus its rows (`csv`); several tables
are several sheets, and a `csv` that stacks them, each under its name, separated by
a blank line. Sheet names come from the heading each table sits under.

A report whose body contains no table MUST be refused with `not_supported` and a
reason that names the missing table, rather than exported as a one-column sheet of
prose lines: that is a spreadsheet in name only, and an author who asked for `xlsx`
and got it would send the wrong file to whoever reads it. Substituting a different
format is likewise forbidden — a failed step is recoverable, a wrong artifact is
not.

Portable output: `{ "report_artifact_id": string, "filename": string, "content_type": string, "format": string, "size_bytes": number, "sha256": string, "download_url": string, "base64": string }`.
Default idempotency: `idempotent`.

## `email_send`
Already compatible: `to`, `cc`, `bcc`, `subject`, `body`, `html`, `from_address`,
`from_name`, `reply_to`, `attachments[{file_name, content_base64, content_type}]`,
`channel` **C** (channel name/slug) / `channel_id` *A*. A relay that cannot honour a
field fails with `not_supported`, never drops it silently.
Default idempotency: `non_reversible`.

## `slack_message`
| key | notes |
|---|---|
| `channel` **C** | the Slack destination: `#name` or channel id |
| `text` **C**, `thread_ts` **C**, `blocks` | message |
| `via` | extension: name of the messaging channel record to post through; default: the instance's default Slack channel record, or the deployment token. FlowWeaver posts with the deployment's token and ignores it |

Portable output: `{ "ok": bool, "ts": string|null, "channel": string, "error": string|null }`.
Default idempotency: `non_reversible`.

## `ansible_playbook`
Playbook YAML on the snippet's `code`. Keys: `hosts` **C** (inventory device
names or `all` for the run's targets), `device` *A* (single), `host` *A* (literal
address), `targets` *A*, `extra_vars`, `timeout_seconds`.

**The whole resolved payload travels to the play as variables**, so a playbook reads any
runtime parameter the node supplied through an ordinary `{{ key }}` lookup. The targeting
and timeout keys are among them; `extra_vars` is one key beside the rest, not a separate
channel that shadows them.

Authentication material is **not** among the variables: it travels by the inventory file
and the process environment. A device resolved from inventory contributes its attributes
(name, address, platform, vendor, os version, site, role, status, properties), never its
credentials.

Portable output: `{ "ok": bool, "changed": bool, "stdout": string, "stats": object }`.
Default idempotency: `non_reversible`.

## `email_mailbox` (extension)
Reads a mailbox. Not implemented by FlowWeaver. A bundle using it declares it in
`requires.snippet_types`; an importer without the handler refuses.

## `netconf`, `snmp_v3`
Registered as snippet types in FlowWeaver, whose handlers do not execute them: a
node using them fails with `not_implemented`.

## Conformance
Family `snippets` (vectors/snippets/): per type, a set of `(input, expected
normalized input)` pairs exercising every alias, and probe vectors that run the
type's real handler on a payload and assert which portable fields its output
carries (see `../adapters/README.md`). Like every vector, they are written from this
text, not recorded from a run.
