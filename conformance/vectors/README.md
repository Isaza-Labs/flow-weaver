# Vectors

Pure-JSON `input -> expected` cases, each declaring an `equivalence` mode and optional
`normalize` rules (kit spec §2.5). Eight families, by risk of silent drift:

- **schema/** — structural validity. Accepts valid `workflow.v1` docs, rejects invalid ones with
  the same error codes.
- **canonicalization/** — canonical form + `SchemaHash` *relations* (not literal hash values):
  equivalent serializations hash equal; a structural edit changes the hash. A divergence here
  hides worst (different hashes → divergent simulation staleness).
- **compiler/** — `workflow.v1` → compiled plan (`yaml-normalized`).
- **executor/** — execution behavior: edge firing and `stop_on_failure`, idempotency tiers, the
  rollback plan and `final_state`, subflow outcomes, and the **audit.v1 event shape**.
- **gate/** — promotion state machine + codes.
- **bundle/** (1.1) — import outcomes of a v3 bundle and the §8 round trip.
- **snippets/** (1.1) — per-type canonical input keys and aliases, and portable output.
- **templates/** (1.1) — template resolution and condition evaluation.

## Anatomy

```json
{
  "id": "executor.rollback.non_reversible_change_makes_final_state_failed",
  "family": "executor",
  "contract_version": "1.1.0-draft",
  "description": "What the SPEC says, and why it matters.",
  "input":  { "...": "canonical input (workflow.v1 or fragment + simulated context)" },
  "expected": { "...": "expected output/behavior" },
  "equivalence": "subset",
  "normalize": ["redact:event_id", "redact:timestamp"],
  "not_implemented": false,
  "oracle_commit": "fw@<sha>",
  "notes": "Ambiguity resolved by reading the oracle, with the file:line read."
}
```

`not_implemented: true` is the **only** legitimate skip: the vector itself declaring that the
behaviour is not required of an implementation yet. An adapter that simply has nothing to say for
a family is a **failure** — see `../adapters/README.md`.

## Where an `expected` comes from

**From the normative text, always.** A vector encodes what the SPEC says, never what an
implementation happens to do. Running a product and recording its output is the kit spec's §4.3
anti-pattern: it produces a vector that can never fail, and it is precisely how a round-trip
check ended up comparing one function's output to the same function's output.

Where the spec is genuinely silent or ambiguous, read the oracle (`flow-weaver`, read-only), use
what it does, and say so in `notes` with the file:line read.

**Write the vector first, run it second.** When a vector then fails, decide which side is wrong.
If the implementation is wrong and the spec is unambiguous, fix the implementation. If it is a
real design question, or the fix is large, or the spec itself looks wrong — do **not** bend the
vector to match the code, and do not add it red. Park it in `<family>/_pending/` with a `notes`
field explaining the conflict. The runner skips `_pending/` entirely, so the suite stays green
and the disagreement stays visible instead of being settled by whoever was closest to it.

Changing a landed vector is a contract change (kit spec §4.4) — never auto-regenerated to turn a
red green.

## What a vector may and may not assert

The line worth holding: assert the contract, not the implementation.

- **Topological order is contract; a total order is not.** The relative order of two independent
  nodes belongs to whichever queue the walker uses. Assert *what* ran (`results`) rather than
  *in what order* (`steps`) unless the graph really fixes it.
- **A wire form is contract; a stored row is not.** `bundle/SPEC.md` §4 says outright: what a
  product stores is its business, what it puts on the wire is the portable identity. A vector
  that pinned the stored value would assert that two products store workflows identically, which
  is not true and is not the point.
- **The presence of a note is contract; its wording is not.** §7 fixes that silence means nothing
  was degraded.
- **Field names are contract; the numbers a handler produced are not.** That is what
  `fields-present` is for.

## Coverage

```
schema 4 · canonicalization 2 · gate 4 · templates 79 · snippets 43 · executor 33 · bundle 18
compiler 0
```

`compiler` is the remaining hole. Everything else is enforced by per-family pass floors in the
consuming repo's runner, because `Failed == 0` is also satisfied by a directory nobody copied
and by a family nobody wrote a vector for.
