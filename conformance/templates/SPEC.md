# workflow.v1 — Template and condition profile

Status: draft · Contract 1.1.0-draft · Oracle: FlowWeaver (this repository)

Templates appear in `config_overrides` values (any depth) and in edge `condition`
strings. This document fixes the whole grammar so a workflow written against one
engine resolves identically in any other conforming engine.

## 1. Namespaces

| reference | resolves to |
|---|---|
| `{{ steps.<node_id>.output<path> }}` | the named step's output (§6 for per-device scoping) |
| `{{ input<path> }}` | the run input payload |
| `{{ device<path> }}` | the current device view (§5); unresolved outside `per_device` |
| `{{ run<path> }}` | run metadata (§7) — capability `run_namespace` |

`<node_id>` matches `[\w-]+`. `<path>` is optional.

## 2. Path grammar
Dotted member access and `[n]` index: `.devices[0].output.stdout`. An empty path
is the root value. A malformed path (`[0` unclosed, `..`) is a **resolution
failure**, not the root value.

## 3. Substitution modes
- **Whole-string**: the value is exactly one `{{ … }}` (surrounding whitespace
  allowed) → the referenced JSON value replaces it **with its type** (objects,
  arrays, numbers, booleans, null).
- **Inline**: any other string → each reference is stringified and spliced.
  Objects/arrays stringify as compact JSON; `null` as empty string.

## 4. Filters — capability `template_filters`
`{{ <ref> | f1 | f2(arg, 'quoted') }}`. Pipes outside quotes/parentheses separate
filters; arguments are comma-separated, quotes stripped.

| filter | effect |
|---|---|
| `default(x)` | the string `x` when the reference is *absent*: it did not resolve, or it resolved to `null`, or to the empty string |
| `trim` · `upper` · `lower` | on strings; non-strings pass through |
| `truncate(n)` | first `n` chars (default 200) |
| `json` | the value as a JSON string |
| `strip_ansi` | remove ANSI escape sequences |
| `strip` | strip ANSI and control chars, then trim |

Only `default` may recover an absent reference; every other filter on an absent
value leaves the whole template unresolved. Unknown filters are ignored.

"Absent" includes the empty string, for every filter — the oracle's rule. It is what makes `{{ steps.x.output.error |
default('none') }}` read the same whether a handler reports "no error" as a
missing key, as `null`, or as `""` — handlers are inconsistent about that and the
author of a template cannot know which they will get. The consequence on the other
filters is the same rule seen from the other side: `{{ x | trim }}` where `x` is
`""` leaves the template literal rather than producing `""`, so an empty value is
visible in the payload instead of silently vanishing into it.

A filter chain is applied left to right, so `default` earlier in a chain feeds the
filters after it: `{{ x | default('n/a') | upper }}` yields `N/A` when `x` is
absent.

## 5. Device view
`{{ device }}` is a fixed projection, never the entity: `id, name, ip, platform,
vendor, os_version, site, role, status, external_id, properties`. `credential_id`,
host-key fingerprints and sync timestamps are never exposed.

## 6. Per-device outputs — capability `per_device_scope`
A `per_device` producer's stored output is the aggregate
`{ "devices": [ { "device_id": "<guid>", "output": <that device's output> } ] }`.
Implementations MAY add fields beside `devices` (for example `per_device, total, failed`)
and inside each entry (`device, success, error, error_code, attempts, duration_ms`);
templates may rely only on `devices[i].device_id` and `devices[i].output`.

When the **consumer** is itself `per_device`, the engine scopes every producer
output that has the aggregate shape to the entry whose `device_id` is the current
device, so `{{ steps.show.output.stdout }}` reads the current device's stdout. A
`once` consumer sees the full aggregate. A `per_device` consumer reading a
producer that has **no** entry for its device resolves to unresolved.

## 7. Run namespace — capability `run_namespace`
`id, workflow_id, workflow_name, environment, trigger, started_at, owner_email,
url, failed_step_id, failed_step_error`. Fields a product cannot provide are
`null`, never absent.

## 8. Unresolved references
Left **literal** in the resolved payload (so the handler and the operator see the
typo). Before executing a step, the engine scans the resolved payload; a residual
`{{ … }}` fails the step with `unresolved_template`, naming the reference — with one
exception: `{{ device.* }}` inside a `once` step is a residual like any other, but
inside the stored **input snapshot** of a `per_device` step it is left literal on
purpose (one snapshot cannot show one value per device).

Secret references `${secret:<source>:<name>:<field>}` are **not** templates: the
resolver never touches them; the handler resolves them right before the wire.

## 9. Conditions (edge `type: "conditional"`)
```
<expr> := <term> ( ('&&' | '||') <term> )*        // && binds tighter; both short-circuit
<term> := '(' <expr> ')' | <value> <op> <value> | <value>
<op>   := == | != | >= | <= | > | <
<value>:= {{ ref }} | number | true | false | null | 'string' | "string" | bare-word
```
Numeric comparison when both sides parse as numbers, otherwise case-insensitive
string comparison. A bare `<value>` is a truthiness test; falsy: `""`, `null`,
`false`, `0`, `[]`, `{}` and an unresolved reference. Any parse error or
unresolved comparison evaluates to **false** (fail closed). A conditional edge also
requires its source step to have succeeded.

References inside conditions use the same `{{ }}` syntax as §1 — a bare
`steps.a.output.x == 1` without braces is a string comparison of the literal text
and is **not** the same thing.

Which namespaces are in scope: `{{ steps.* }}`, `{{ input.* }}` and `{{ run.* }}`
all resolve. `{{ device.* }}` does **not** and always reads as unresolved — an edge
fires once at the DAG level after its source node completes, so there is no single
current device even when the source fanned out. Branching on the run's own input,
`{{ input.apply }} == true`, is the case this exists for: it is how one definition
serves a dry run and a real one.

## Conformance
Family `templates` (vectors/templates/): `(outputs, input, device, run, template) →
expected resolved value or "unresolved"`, and `(context, condition) → bool`. Every
implementation runs the same vectors through its own resolver/evaluator.
