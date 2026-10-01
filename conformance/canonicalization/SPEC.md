# Canonicalization — normative spec

The `SchemaHash` is the fingerprint of a workflow's structure. Two implementations that
canonicalize differently produce different hashes for the *same* logical workflow, and all
simulation-staleness logic diverges silently. This spec is **normative**: the algorithm below
is the contract. It is reified from FlowWeaver's `ComputeSchemaHash`; FlowWeaver (this repository)
is the oracle.

## Input

The workflow's `nodes` array and `edges` array (the two required members of a `workflow.v1`
document). Nothing else participates in the hash — not name, environment, metadata, timestamps.

## Algorithm

```
SchemaHash(nodes, edges) =
    lowercase_hex( SHA-256( UTF8( canonical(nodes) + "|" + canonical(edges) ) ) )
```

`canonical(value)` serializes a JSON value deterministically:

- **object** → `{` then, for each member **sorted by key using ordinal (code-unit) order**,
  the JSON-quoted key, `:`, and `canonical(value)`, members joined by `,`, then `}`.
- **array** → `[` then `canonical(item)` for each item **in document order**, joined by `,`,
  then `]`. (Array order is significant — reordering nodes/edges is a different document only if
  the arrays themselves differ; equivalent documents that differ only by object key order hash
  equal, but element order in arrays is preserved as-is.)
- **string** → the JSON-escaped, double-quoted form (standard JSON string escaping).
- **number** → prefer decimal: the invariant-culture decimal string with **trailing zeros
  stripped** (`1.00` → `1`, `1.50` → `1.5`); if not representable as decimal, the round-trip
  (`"R"`) double form; otherwise the raw token.
- **true** / **false** / **null** → the literals `true`, `false`, `null`.

Separator between the two arrays is a single pipe `|`.

## Rules that follow

- **Object key order is irrelevant** (keys are sorted) — the same logical node/edge hashes
  equal regardless of how its fields were serialized.
- **Array element order is preserved** — the canonical form does not sort nodes or edges. Two
  documents are hash-equal only if their arrays are element-wise canonical-equal. (An
  implementation MAY additionally define a stable node/edge ordering upstream, but that is not
  part of this hash; the hash sees the arrays as given.)
- **Number normalization** removes trailing-zero noise so `1.0` and `1` are the same.
- Whitespace and insignificant JSON formatting never affect the hash (only canonical output is
  hashed, not the source text).

## What the hash is for

After a `simulate_workflow_run`, the result stores this `SchemaHash`. The promotion gate
(draft → qa) recomputes it from the workflow's current `nodes`/`edges`; if it differs from the
stored simulation hash, the simulation is **stale** and promotion is refused (`simulation_stale`).
So: **an edit that changes structure changes the hash**, which is exactly what invalidates a
prior simulation. See `../vectors/canonicalization/` (invariants A and B) and `../vectors/gate/`.

## Reference implementation

FlowWeaver implements this in `flow_weaver_backend/Services/Workflow/WorkflowCanonicalizer.cs`
(`ComputeSchemaHash`, with `Canonical` exposing the canonical form), covered by unit tests and by
the `canonicalization` conformance family. The vectors assert **relations** (equivalent → equal
hash; edited → different hash), never a literal hash value — pinning a literal would couple the
contract to one implementation's output.
