# Skill: authoring `python_snippet` code

`python_snippet` runs a user-authored Python script per step. The body
lives on `Snippet.Code`. This skill documents what scripts can do, how
they call integrations, and the I/O shape.

## Runtime contract

- Worker runs `python3 <tempfile>` per step.
- `InputPayload` (config_overrides + resolved templates) arrives on
  **stdin as JSON**.
- Whatever the script writes to **stdout** is parsed as JSON. If invalid,
  the handler wraps it as `{ "raw": "<stdout>" }`.
- Timeout: `snippet.timeout_seconds` (default 60s, clamped 5–600).

### Reading upstream step outputs

For `python_snippet` the orchestrator auto-injects every upstream step
under `inp['steps']`. You do **not** need to template each upstream
through `config_overrides` first — that injection is exclusive to
`python_snippet` (other handlers ignore the bag, so they receive their
data via `{{ … }}` templates as before).

`get_input()['steps']['<node-id>']['output']` is the upstream node's
output. The shape depends on the producer/consumer `target_mode`:

| Producer    | This snippet | What `output` contains                                |
| ----------- | ------------ | ----------------------------------------------------- |
| `once`      | any          | The handler's raw output dict.                        |
| `per_device`| `once`       | The aggregate envelope (`devices[]`, `devices_by_name`, `first_success`, counters). |
| `per_device`| `per_device` | Auto-scoped to **this** device's own output dict (the envelope is unwrapped). |

For the once consumer of a per_device producer, the envelope is:

```python
inp = get_input()
ssh = inp['steps']['ssh-show-version']['output']

ssh['devices']                         # list[dict] — every per-device entry
ssh['devices_by_name']['router-1']     # dict       — same entry, keyed by hostname
ssh['first_success']                   # dict|None  — first entry where status == "completed"
ssh['success_count']                   # int
ssh['failed_devices']                  # list[str]  — hostnames

# Each entry exposes three name aliases (all equal):
entry = ssh['devices_by_name']['router-1']
entry['device']                        # canonical
entry['device_name']                   # alias
entry['hostname']                      # alias
entry['status']                        # "completed" | "failed" | "skipped" (lowercase, case-sensitive)
entry['output']                        # the per-device handler output
```

Common mistake: writing a per_device aggregator that reads
`inp['steps']['ssh']['output']['devices']`. The orchestrator unwraps the
envelope per-device, so `'devices'` isn't there and the loop sees an
empty list. Aggregators that summarise across the whole fan-out must
declare `target_mode: once`.

**Be defensive about values, loud about shape.** `item.get('device_name',
'unknown')` is fine — a missing name is cosmetic. The envelope itself is
not: if the key you loop over isn't there, a `.get(key, [])` silently
turns a wiring bug into an empty result that exits 0. Assert instead:

```python
ping = inp['steps']['ping-devices']['output']   # KeyError if the node id is wrong
if 'devices' not in ping:
    raise ValueError(f"expected a per_device envelope, got keys: {sorted(ping)}")
```

An aggregator returning `{"rows": [], "counts": {…zeros}}` because it
read the wrong key is indistinguishable from one that ran against an
empty inventory — and every downstream node will happily render either.
Same rule for `| default(...)` in templates: see `Skills/workflows.md`
→ Filters.

**Status strings are lowercase and case-sensitive.** `entry['status']`
mirrors the `StepStatus` constants in `Services/Engine/Status.cs` —
always `"completed"` / `"failed"` / `"skipped"` / `"pending"` /
`"running"` / `"cancelled"`. Comparing against `"Completed"` (capital C)
silently fails for every device, which then cascades into false
"step failed" branches downstream. Same rule for `RunStatus` and
`JobStatus`.

### Reading the current device

For a **per_device** `python_snippet` (and a `once` step run against a
single target) the orchestrator injects the current device under
`inp['device']` — the same shape as the `{{ device.X }}` templates:

```python
inp = get_input()
dev = inp['device']            # present for per_device / single-target steps
dev['name']                    # e.g. "node-l1"  ← use this for the filename
dev['ip']                      # management IP
dev['platform']                # e.g. "nokia_srl"
dev['vendor'] / dev['site'] / dev['role'] / dev['os_version']
dev['properties']              # custom fields (dict)
```

Use `inp['device']['name']` for per-device filenames — **do NOT** emit
`"unknown-device"` as a fallback, and you do NOT need to thread
`{{ device.name }}` through `config_overrides`; the object is injected
automatically. For a `once` step with multiple targets there is no single
current device, so `inp.get('device')` is absent — read names from the
upstream envelope's `devices[]` instead.

### SSH output is ANSI-stripped

`steps.<ssh-node>.output.stdout` and each `results[i]['output']` arrive
already cleaned of ANSI/CSI/OSC and C0 control chars (Nokia SR-OS,
Cisco IOS-XR with paging, screen sessions). The raw bytes are under
`stdout_raw` / `results[i]['output_raw']` if you actually need them.

Use `flowweaver_runtime` instead of reading stdin raw:

```python
from flowweaver_runtime import get_input, set_output

def run(ctx):
    set_output({"echo": get_input()})
```

### Entry point — `run(ctx)` is auto-invoked

`flowweaver_runtime` registers an `atexit` hook that calls a module-
level `run(ctx)` if defined AND `set_output()` wasn't already called.
**Do NOT add a trailing `run(None)`** — the runtime does it.

`ctx` is always `None` today (reserved). Read runtime data via
`get_input()`.

Equivalent forms — pick one and stick with it:

```python
def run(ctx):
    set_output({"echo": get_input()})
```

```python
set_output({"echo": get_input()})  # module-level
```

**Pitfall**: defining `def run(ctx):` AND calling `set_output()` at
module level → the module-level call wins; `run` is skipped (auto-invoke
short-circuits once `set_output` ran).

**An exception inside `run(ctx)` fails the step** (non-zero exit, the
traceback in the step's Logs), the same as from module-level code. So
`{"raw": ""}` on a *successful* python step means exactly one thing: the
script exited without ever calling `set_output()`. Read the step's Logs —
if a traceback is there, the step also failed and the message names the
line. Do not paper over it with `.get(key, [])`: fix the key, the
`<name>_integration_id`, or the upstream shape the script assumed.

## Mermaid diagram — MANDATORY on every create/update

`python_snippet` create/update payloads MUST include
`logic_diagram_mermaid`. The backend rejects with `logic_diagram_invalid`
when missing or not a Mermaid directive.

```json
{
  "name": "dns-reconcile",
  "type": "python_snippet",
  "target_mode": "once",
  "code": "from flowweaver_runtime import ...",
  "logic_diagram_mermaid": "flowchart TD\n    in([rows]) --> loop{...}\n    out([rows with status])"
}
```

`python_snippet` behavior is opaque (unlike `ping` / `ssh` which are
self-descriptive). The diagram lets readers understand the task without
opening Python. A diagram that doesn't match the code is worse than
none — update both together. Conventions: `Skills/mermaid.md`.

## Sandbox — import allowlist

Static import analysis runs before execution. Imports outside this list
fail with `Blocked: script imports disallowed module '<name>'`:

| Allowed | Why |
|---|---|
| `json`, `math`, `datetime`, `re`, `ipaddress` | pure stdlib, no I/O |
| `flowweaver_runtime` | stdin/stdout + integration bridge |
| `csv`, `io`, `base64` | build CSV bytes + encode for email attachments |
| `string`, `collections`, `itertools` | tabular transforms, `Counter`, `groupby`, `chain` |
| `hashlib` | content-hash dedup / idempotency keys |
| `time` | `time.monotonic()` deadlines, short `time.sleep()` |

Also blocked: `exec(`, `eval(`, `__import__`, `open(`,
`requests`, `urllib`, `urllib.parse`, `socket`, `subprocess`, `os`,
`typing`, `__future__`, `logging` (the last one is allowed only in network-enabled snippets).

The AST guard also blocks some **names** wherever they appear, not only when
they are called: `input`, `open`, `compile` (so `re.compile` is out), `eval`,
`exec`, `getattr`, `setattr`, `vars`, `locals`, `globals`. A variable called
`input` is refused, so call it `inp`. If you are adapting a script the user
pasted, work through the porting checklist in `Skills/netmiko_snippets.md`
first; it applies to every python_snippet, not only network-enabled ones.

A plain python_snippet has **no network at all**, and that includes
`integration(name)`. See *Calling a registered Integration* below for what
that means and what to do instead.

**Extra modules (admin-managed).** Beyond the table above, an admin can allow
more imports at `/admin/python-packages` (a stdlib module, or a PyPI
package the worker installs). If a snippet you want to write needs a module not
in the list, **you cannot add it yourself** — it's admin-only. Tell the user an
admin must add the import there first (note the import name vs the pip package
name can differ, e.g. import `bs4` ← install `beautifulsoup4`). The
`exec`/`eval`/`open`/`__import__` bans and the no-network rule still apply to
those modules too — adding `requests` only works inside a `network_enabled`
snippet.

## Building CSVs and email attachments

Canonical pattern for "iterate, build a CSV, hand to a downstream
email step". It calls `integration()` in a loop, so it only runs in a
**network-enabled** snippet, which an admin has to flag (see *Calling a
registered Integration* and say so in the plan). If the data can come from
upstream `integration_action` nodes, read it from `inp['steps']` instead.
NetBox paths here assume `base_url` is the NetBox root, so they start with
`/api/`:

```python
from flowweaver_runtime import get_input, set_output, integration
import csv, io, base64

def run(ctx):
    inp = get_input()
    netbox = integration("netbox")
    infoblox = integration("infoblox")

    resp = netbox.get("/api/dcim/devices/", params={"status": "active", "limit": 500})
    if not resp.ok:  # the client has .ok / .status_code / .text / .json(); no raise_for_status()
        raise RuntimeError(f"NetBox HTTP {resp.status_code}: {resp.text[:200]}")
    devices = resp.json().get("results", [])

    rows = []
    for d in devices:
        name = d["name"]
        ip = (d.get("primary_ip4") or {}).get("address", "").split("/")[0]
        if not ip:
            continue
        ib = infoblox.get("/record:host", params={"name": name})
        in_sync = ib.ok and bool(ib.json())
        netbox.patch(f"/api/dcim/devices/{d['id']}/",
                     json={"tags": [{"slug": "dns-verified" if in_sync else "missing-dns"}]})
        rows.append([name, ip, "In Sync" if in_sync else "Missing DNS"])

    buf = io.StringIO()
    writer = csv.writer(buf)
    writer.writerow(["device_name", "ip_address", "dns_status"])
    writer.writerows(rows)

    set_output({
        "row_count": len(rows),
        "filename": f"dns-reconciliation-{inp.get('today', '')}.csv",
        "content_type": "text/csv",
        "content_base64": base64.b64encode(buf.getvalue().encode("utf-8")).decode("ascii"),
        "rows": rows,  # optional — feed an inline `report` node
    })
```

A downstream `email_send` node consumes them as an attachment — both
plain strings, no template gymnastics (note `email_send` names the
field `file_name`):

```json
"attachments": [
  { "file_name":      "{{ steps.<this>.output.filename }}",
    "content_base64": "{{ steps.<this>.output.content_base64 }}",
    "content_type":   "text/csv" }
]
```

**Never send the email yourself from Python.** Use the native
`email_send` node (SMTP channels from `/email`) — see `Skills/email.md`.

### Pitfall: NetBox 4.x related-object writes (tags, primary_ip, etc.)

NetBox 4.x rejects bare-string references for any related object on
write. The error is:

```
{"tags":["Related objects must be referenced by numeric ID or by
         dictionary of attributes. Received an unrecognized value: <slug>"]}
```

Always wrap related-object writes in a dict (or pass the numeric id):

```python
# ❌ Will be rejected by NetBox 4.x
nb.patch(f"/api/dcim/devices/{id}/", json={"tags": ["missing-dns"]})

# Accepted — slug as a dict
nb.patch(f"/api/dcim/devices/{id}/", json={"tags": [{"slug": "missing-dns"}]})

# Also accepted — name as a dict
nb.patch(f"/api/dcim/devices/{id}/", json={"tags": [{"name": "missing-dns"}]})

# Also accepted — numeric id (cheapest if you already have it)
nb.patch(f"/api/dcim/devices/{id}/", json={"tags": [625]})
```

Same rule applies to **every** related field: `role`, `site`,
`platform`, `tenant`, `device_type`, `primary_ip4`, `cluster`, etc.
Reads return objects with `id`/`slug`/`name`; writes need the same
shape (or just the id) — passing the display string fails.

When merging existing tags with a new one, normalise to dicts so the
final payload is consistent:

```python
def with_tag(existing, new_slug):
    slugs = []
    for t in existing or []:
        if isinstance(t, dict):
            s = t.get("slug") or t.get("name")
            if s:
                slugs.append(s)
        elif isinstance(t, str):
            slugs.append(t)
    if new_slug not in slugs:
        slugs.append(new_slug)
    return [{"slug": s} for s in slugs]   # ← always dicts on the way out
```

Read this rule once, apply forever — every NetBox write that touches a
related field needs the dict (or id) form.

## Calling a registered Integration

**`integration()` needs a network-enabled snippet.** The HTTP call is made
from inside the sandbox, and a snippet without `network_enabled` has no
network. On a deployed worker, a step that declares any `<name>_integration_id`
in a snippet without the flag fails before the script starts, with
`this snippet declares integration(s) 'netbox' but is not network-enabled`.
It works in local Development only because that runs without the sandbox.

You cannot set the flag yourself (it is human_only), so pick one of these:

1. **Default: keep the HTTP out of python.** Put the call in an
   `integration_action` node and let a plain python_snippet read
   `inp['steps']['<node>']['output']`. That covers most flows: one call per
   node, then logic over the result.
2. **When the logic really must call the API in a loop** (paginate, look up
   each row, patch back), write the snippet as below, create it without the
   flag, and ask the user to have an admin tick *Network enabled* on it in
   `/snippets/<id>`. Say this in the plan, before the first run fails. Do not
   discover it from the error.

Never hardcode base URLs or tokens. Declare integrations in
`config_overrides` using the `<name>_integration_id` convention.

### On the workflow node

```json
{
  "id": "dns-audit",
  "snippet_id": "<python_snippet uuid>",
  "config_overrides": {
    "netbox_integration_id":   "<netbox-uuid>",
    "infoblox_integration_id": "<infoblox-uuid>",
    "active_status": "active",
    "sync_tag":      "dns-verified"
  }
}
```

Any key ending in `_integration_id` is pre-resolved; the prefix
(`netbox`) becomes the handle for `integration("netbox")`. Value must
be a UUID (preferred — survives renames) or an exact active integration
name (case-insensitive — tolerated only for hand-edited workflows).

Bad value → `IntegrationError: integration 'X' not available — known: ...`
at the `integration("X")` call. Fix the value in the node config.

### Inside the script

```python
from flowweaver_runtime import get_input, set_output, integration

def run(ctx):
    cfg = get_input()
    nb = integration("netbox")
    resp = nb.get("/api/dcim/devices/", params={"status": cfg["active_status"], "limit": 500})
    set_output({"count": len(resp.json().get("results", []))})
```

### Client API

`integration("<name>")` returns an object with `.get`, `.post`, `.put`,
`.patch`, `.delete` — same signature:
`(path, json=None, params=None, headers=None, timeout=30)`.

Each call returns a `_Response` with `.status_code`, `.ok` (2xx/3xx),
`.text`, `.headers`, `.json()` (raises `IntegrationError` if not JSON).

What the client enforces:
- **Path is always relative to the integration's `base_url`** —
  `http://...` / `//...` raises `IntegrationError` before any request
  (no SSRF escape).
- **Auth is injected automatically** from the integration's `AuthConfig`.
  Scripts never see the raw token.
- **TLS** follows `integration.tls_skip_verify`.

When `integration()` raises:
- Name not in config_overrides → `IntegrationError: integration 'X' not available`.
- Network error → `IntegrationError: ... request failed: ...`.
- Non-2xx → **does NOT raise**; check `.ok` / `.status_code` explicitly.

## Non-integration secrets — `${secret:...}`

For credentials that aren't an Integration (plain SSH credentials,
standalone API keys, AI provider keys), reference from
`config_overrides`:

```json
{
  "config_overrides": {
    "device_username": "${secret:credential:core-router-prod:username}",
    "device_password": "${secret:credential:core-router-prod:password}",
    "slack_token":     "${secret:secret:slack_bot:value}"
  }
}
```

Sources: `credential` (`username`/`password`/`private_key`), `secret`
(`value`), `ai_provider` (`api_key`).

The worker resolves these **before** subprocess starts — the script
sees plaintext directly. Unresolved tokens (typo'd name) stay literal
so the failure is obvious.

**Don't** wrap `${secret:...}` inside the snippet's `Code` field —
the resolver only walks the JSON input payload, not Python source.

## Structured logging

```python
from flowweaver_runtime import log

log("info", "started audit", active_status="active")
log("warning", "device skipped", name=dev["name"], reason="no primary_ip4")
log("error", "infoblox unreachable", host=name)
```

Levels: `debug`, `info`, `warn`/`warning`, `error`. Each entry gets an
ISO-8601 UTC timestamp added by the runtime.

Mirrored to the step's `Logs` (stderr) AND `OutputPayload._logs`.
Downstream nodes read via `{{ steps.<X>.output._logs }}`. Scalar
outputs are wrapped as `{ value: <original>, _logs: [...] }`.

## Forbidden patterns — backend-enforced

`SnippetService.DetectScaffoldCode` rejects (400) on save when `Code`
contains any of these markers AND makes no real `integration(...)` /
`subprocess` / `socket` call:

- `scaffold snippet` / `placeholder snippet`
- `todo: implement` / `fixme: implement`
- `not yet implemented` / `replace this placeholder`

The gate catches code like:

```python
# blocked:
set_output({"status": "placeholder", "message": "Scaffold for LLDP sync"})
```

— a step that exits 0 without doing real work, leaving downstream nodes
to consume fiction.

When you genuinely can't implement the step:
1. **Tell the user and stop.** Almost always the right answer.
2. **Save an explicitly-failing skeleton** that raises:

```python
raise NotImplementedError(
    "LLDP→NetBox sync not implemented: missing vendor schema mapping. "
    "Ask the admin to paste example neighbor JSON before this step can run."
)
```

The step fails loudly with a clear message; no placeholder marker → the
gate allows it.

## Anti-patterns

- **Don't paste a bare identifier at the end of the script.** A
  dangling `lldp_collect_and_sync` line is either `NameError` or a
  no-op lookup — never an invocation. `run(ctx)` is auto-invoked.
- **Don't reach for `python_snippet` when a specialized snippet exists.**
  HTTP → `integration_action`. SSH → `ssh`. Reports → `report`. ICMP
  → `ping`. Email → the native `email_send` snippet (see
  `Skills/email.md`). Slack → `slack_message`. Reserve `python_snippet`
  for custom logic those don't cover (vendor parsing, diffs, transforms).
  - **Exception — INTERACTIVE SSH** (a prompt you must answer mid-session,
    e.g. a password change that asks to confirm, commit-confirm, `[y/n]`):
    the `ssh` step can't do prompt/response. Use a **network-enabled
    python_snippet** with netmiko/paramiko — see `Skills/netmiko_snippets.md`.
    You cannot set `network_enabled` yourself (human_only). Reuse the seeded
    `SSH primitive (paramiko)`, which also covers Windows/PowerShell over SSH,
    or draft the snippet and have an admin tick the flag in the UI.
  - **Public HTTP API with no Integration** (NVD, a vendor advisory feed):
    a `rest_call` node does the request and a python_snippet parses its
    `body`. See *Recipe: NVD CVE lookup* below. Do not use `httpx`/`requests`.
- **Don't hardcode URLs or tokens** — route through `integration(name)`.
- **Don't swallow exceptions** — surface them. The worker tags the step
  failed when `run()` raises; bare `try/except: pass` hides bugs.
- **Don't loop unbounded over paginated APIs** — respect `limit=500`
  and follow `next` cursors.

## Recipe: NVD CVE lookup by CPE

The NVD 2.0 API is public HTTPS, and a `rest_call` node reaches it with no
python networking. Split the work in two: the HTTP call, then the parsing.

**`nvd-cves`**: a `rest_call` snippet. The seeded `rest_call` is
`target_mode: once`. To look up one CPE per device (for example after
`parse-windows-audit` in `Skills/netmiko_snippets.md`), create your own with
`type: "rest_call", target_mode: "per_device"`. Node `config_overrides`:

```json
{
  "method": "GET",
  "url": "https://services.nvd.nist.gov/rest/json/cves/2.0?cpeName={{ steps.parse-windows-audit.output.cpe }}&isVulnerable",
  "headers": { "apiKey": "${secret:secret:nvd_api_key:value}" }
}
```

Three details about NVD. Each one causes a 404 or a 403 that looks like a bug
in your code:

- `isVulnerable` is a **bare flag**. `isVulnerable=true` returns 404.
- `cpeName` needs part, vendor, product and version, with no `*` in those four.
  Some products were renamed: Windows 10 is split per release
  (`windows_10_22h2`), not `windows_10:<release>`. The NVD CPE dictionary is
  authoritative.
- Without an API key the limit is 5 requests per 30 s, and going over returns
  **403**. Set the node's `max_parallel: 1`. If the user has no key, leave the
  `headers` out and tell them the fan-out will be slow. Do not add `time.sleep`
  to a python step to compensate.

`rest_call` fails the step on a non-2xx response. For a device whose `cpe` came
back `null`, the URL has an empty `cpeName`, so that device fails here with a
404 instead of silently reporting zero CVEs.

**`nvd-cves-parse`**: python_snippet, same `target_mode` as `nvd-cves`,
`changes_state: false`:

<!-- guard-check: base -->
```python
import json
from flowweaver_runtime import get_input, set_output

inp = get_input()
resp = inp["steps"]["nvd-cves"]["output"]
if "body" not in resp:
    raise ValueError(f"expected a rest_call output with 'body', got keys: {sorted(resp)}")

data = json.loads(resp["body"])
cves = []
for item in data.get("vulnerabilities", []):
    cve = item["cve"]
    descriptions = cve.get("descriptions", [])
    detail = next((d["value"] for d in descriptions if d.get("lang") == "en"),
                  descriptions[0]["value"] if descriptions else "")
    metrics = cve.get("metrics", {})
    score = None
    for key in ("cvssMetricV31", "cvssMetricV30", "cvssMetricV2"):
        if metrics.get(key):
            score = metrics[key][0]["cvssData"].get("baseScore")
            break
    cves.append({"id": cve["id"], "score": score, "detail": detail})

cves.sort(key=lambda c: c["score"] or 0, reverse=True)
set_output({
    "total": data.get("totalResults", len(cves)),
    "returned": len(cves),
    "cves": cves,
})
```

Diagram: `flowchart TD\n in([steps.nvd-cves.output.body]) --> j[json.loads] --> l[per vulnerability: id, English description, best CVSS score] --> s[sort by score desc] --> out([total, returned, cves])`

NVD pages at 2000 results (`resultsPerPage` / `startIndex`). One OS build
usually stays under that. If `total > returned`, say so in the report rather
than presenting the list as complete.

## Editing an existing snippet

1. `fw_snippets:get_snippet(id)` — read current code.
2. Plan the rewrite: list integrations, expected `config_overrides`
   keys, output shape downstream consumes.
3. `fw_snippets:update_snippet(id, { code: "...", logic_diagram_mermaid: "..." })`
   — keep diagram in sync.
4. Update workflow nodes that reference this snippet so their
   `config_overrides` carry the right `<name>_integration_id` keys.
