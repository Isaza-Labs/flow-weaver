# Adapters

An **adapter** is the thin, language-specific shim each implementation writes to connect its
engine to the vectors. It is the *only* language-specific piece and it **lives in the
implementation's repository, not in the kit**. The kit stays pure data so any third-party
implementation (or a community UI) can verify against the same vectors without depending on
.NET.

## Responsibility

Given a vector's `family` and `input`, the adapter:

1. Feeds `input` to the corresponding engine entry point.
2. Normalizes the engine's raw output into the shape the vector's `expected` uses.
3. Returns that normalized object (the runner then applies `equivalence` + `normalize`).

The adapter does **not** decide pass/fail and does **not** apply equivalence — that is the
runner's job. Keeping the adapter dumb keeps the contract honest.

## Methods (one per family)

| Family | Adapter input | Adapter output |
|---|---|---|
| `schema` | a `workflow.v1` document | `{ valid: bool, errors: [{ keyword, path }] }` |
| `canonicalization` | `{ variants: [...] }` or `{ before, after }` | `{ all_schema_hashes_equal, all_canonical_forms_equal }` or `{ schema_hash_changed }` |
| `compiler` | a `workflow.v1` document | `{ compiled: <plan/YML> }` |
| `executor` | `{ workflow, context }` | `{ status, final_state, steps, results, rollback_plan, errors, tiers, outputs, audit_events }` |
| `gate` | `{ workflow_state, simulation, action, ... }` | `{ http_status, code }` |
| `bundle` (1.1) | `{ bundle, local?, round_trip? }` | `{ outcome, codes, schema_hash, notes, notes_empty, created, workflow, triggers, requires, nodes, node_overrides, node_override_keys }` (+ `schema_hash_unchanged`, `exported_node_overrides`, `exported_node_override_keys` on a round trip) |
| `snippets` (1.1) | `{ type, input }` or `{ type, probe }` | `{ normalized_input }` or `{ output }` |
| `templates` (1.1) | `{ outputs, input, device, run, consumer_target_mode, template }` or `{ context, condition }` | `{ resolved: <json> \| "unresolved" }` or `{ result: bool }` |

### `templates`

`device` is given as the **eleven-field projection of §5** plus whatever a product holds beside
it — `credential_id`, a host-key fingerprint, a sync timestamp. The adapter loads it into a real
inventory row and asks the engine for the view, so `{{ device }}` is answered by the product's
own projection and a vector can assert the projection is exactly those eleven names, with the
rest absent.

`run` is given as whatever the product can supply; the adapter builds the engine's own run
context from it, so a vector asserts the real ten-field projection and §7's *null, never absent*
rule rather than the object the vector happened to carry.

`resolved` is `"unresolved"` when a residual `{{ … }}` survived resolution — §8's rule is that an
unresolvable reference is left literal and the step then fails, so "a residual is left" is the
observable.

The condition form **never** passes a device view to the evaluator. §9 is explicit that
`{{ device.* }}` does not resolve in a condition — an edge fires once at DAG level after its
source completes, so there is no single current device even when the source fanned out. A vector
may still put a `device` in the context; the point is that it is not used.
`executor.conditional.device_reference_does_not_fire` covers the same rule end to end.

### `snippets`

`{ type, input } -> { normalized_input }` is the payload **after alias normalisation, before
execution**: the canonical keys of `snippets/SPEC.md`, with the alias dropped when the canonical
key is present (`bundle/SPEC.md` §4).

`{ type, probe } -> { output }` **runs the type's real handler** on `probe` — normalised first,
exactly as the node executor does — and returns what it emitted. A vector that carried a captured
output and then asserted which fields *that same output* has could never fail: it would agree
with itself. Running the handler is what turns "these are the portable fields" into a claim about
the implementation — and it is what lets an alias vector assert the tool that was **actually
invoked** rather than the key the payload happened to carry.

Fakes sit at the same boundary the product's own handler tests use. A type with no probe harness
throws rather than answering quietly; add a harness, or write the vector in the `{ type, input }`
form.

### `executor`

`context` scripts the run: `nodes.<id>` = `{ result, tier, error_code, output }`,
`subflows.<id>` = the outcome of the child run a `subflow` node started, plus `stop_on_failure`
(default true), `input`, `run`, and `workflow_id` / `schema_hash` / `actor` for the audit events.

What must be **real**: the DAG walk, edge firing, stop-on-failure, the audit events, the rollback
plan, the final state, condition evaluation, and every `subflow` node — `subflow_missing`, the
cycle guard, the output envelope and the strictest-child-tier rule are engine answers, not
harness answers. The only thing scripted is what a handler *did*, which is exactly what the
vectors are parameterised over and is the boundary the node-executor interface exists to make
swappable.

`steps` is the ordered walk; `results` is the same outcomes keyed by node, for the cases where
the contract fixes **what** ran and not the total order. Topological order is contract; the
relative order of two independent nodes is not, and a vector that asserted it would pin an
implementation detail.

### `bundle`

`local` is what the **receiving instance** already holds — `snippet_types` (the handler
registry), `integrations` (with their `actions`), `credentials`, `repositories`, `mcp_servers`.
It has to be part of the vector: §5 refuses a bundle whose dependencies are missing, so
"refused" and "imported" are answers about a *pair* (bundle, instance), and a vector that named
only the bundle would be asserting half a question.

`codes` is the **set** of refusal codes the failure names, not just the most structural one:
§7 says a refusal names every missing dependency, unsupported capability and untranslatable key
at once, so an adapter that surfaced only the first would let the other two rot.

`schema_hash` is §8's hash — over the **bundle's own wire nodes+edges**, with `snippet_id` and
`subflow_workflow_id` replaced by ordinal placeholders (each id by its order of first
appearance), never over the stored row. The two are deliberately different things: the
row may carry whatever local vocabulary the product runs on, while the wire form carries portable
identities only. Comparing stored rows would assert that two implementations store workflows
identically, which is not the contract and is not true.

`notes_empty` is what a vector should assert about notes, not their wording: §7 fixes that
*silence means nothing was degraded*, and leaves the text of each note to the product.

`node_overrides` / `node_override_keys` expose the stored node payloads and their **exact key
set** — the key set is what proves a legacy id key was dropped rather than left lying beside the
canonical name, an absence no "these fields are present" comparison can see.
`exported_node_overrides` is the same thing after a re-export, which is the only place §4
constrains the *value*: what a product stores is its business, what it puts on the wire is the
portable identity.

## `not_implemented` — the only skip

**An adapter returning nothing is a FAILURE, not a skip.** A vector whose family the adapter does
not handle fails, and the report names it. The only legitimate skip is one the **vector itself**
declares:

```json
{ "id": "...", "family": "compiler", "not_implemented": true, "input": { }, "expected": { } }
```

That is the kit's documented escape hatch, and it is deliberately noisy: it lives in the
contract, under review, rather than in a shrug from a harness nobody reads. If adapter silence
counted as a skip, a family with no behaviour behind it — an unexecuted node type, a rollback
that misreports, a condition that fails open — would report green. Silence is not a skip.

## FlowWeaver's adapter

FlowWeaver's adapter is `flow_weaver_backend.Tests/Conformance/FlowWeaverAdapter.*.cs`: the
`schema` and `canonicalization` methods live beside the runner in `ConformanceRunner.cs`, and
`bundle`, `executor`, `gate`, `snippets` and `templates` each have their own
`FlowWeaverAdapter.<Family>.cs` file, calling into `flow_weaver_backend`. It reaches into the
engine — that is the one place the kit touches implementation — but it only *observes* behavior,
never encodes structure into a vector. It does not answer the `compiler` family (which has no
vectors) or the `{ type, probe }` form of `snippets`; vectors in that form fail against it.

### Runner capabilities FlowWeaver implements

FlowWeaver's runner implements the equivalence modes `exact`, `normalized`, `subset` and
`fields-present`, and the normalize rules `redact:<field>` and `sort:<field>`, as defined in
`../ci/run-conformance.md`. An unknown `normalize` rule makes it throw rather than be ignored.

`yaml-normalized`, `set-equal:<field>`, `partial-order` and `numeric-tolerance:<eps>` are not
implemented. No landed vector uses them; FlowWeaver's runner compares any mode it does not
recognise as `exact`.
