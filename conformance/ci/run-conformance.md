# Conformance runner contract

The runner is the harness each product ships to check its engine against the pinned kit. The
kit defines the **contract** the runner must honor; the runner itself lives in each repo (it is
language-specific, like the adapter). Nashira's runner lives under
`nashira_backend.Tests` / a dedicated conformance project and is invoked from CI.

## Inputs

- The pinned kit version (this directory), specifically `vectors/**/*.json` and
  `schema/workflow.v1.schema.json`.
- The product's **adapter** (see `../adapters/README.md`), which maps a vector's `input` to its
  engine and normalizes the engine's output to the vector's `expected` shape.

## Per-vector algorithm

For each vector file:

1. Read the vector (`id`, `family`, `input`, `expected`, `equivalence`, optional `normalize`).
2. Dispatch `input` to the adapter method for `family`.
   - An adapter that cannot answer the family **FAILS** the vector. Silence is not a skip: an
     adapter that quietly returned nothing is how a family with no implementation behind it
     reported green, which is the failure this kit exists to make impossible.
   - The **only** legitimate skip is a vector that declares `"not_implemented": true` about
     itself. A skip is a statement the CONTRACT makes about what is not required yet, never a
     statement an implementation makes about what it did not get around to.
3. Apply the vector's `normalize` rules to both actual and expected (redact volatile fields,
   sort unordered collections) — see the kit spec §4.2.
4. Compare actual vs expected using the vector's `equivalence` mode (below).
5. Record `pass` / `fail` / `skip` with the vector id and, on failure, the normalized diff.

## Equivalence modes

| Mode | Compares |
|---|---|
| `exact` | Literal equality after normalization |
| `normalized` | Equality after normalization (timestamps/ids redacted) |
| `yaml-normalized` | YAML equality, map key order irrelevant, lists ordered |
| `set-equal:<field>` | Set equality of `<field>` (order irrelevant) |
| `partial-order` | Order constraints only (e.g. a before c) |
| `numeric-tolerance:<eps>` | Equality within tolerance |
| `fields-present` | Presence + type of fields, not their values |

Choosing the wrong mode is the most common error: `exact` over output with timestamps yields
false reds; `set-equal` where order matters (DAG execution) hides real bugs. The mode is a
**contract decision**, reviewed per vector.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | All non-skipped vectors passed |
| `1` | One or more vectors failed (**blocks the merge** in Nashira) |
| `2` | Runner/setup error (bad kit, unreadable vector, adapter crash) |

## Report

The runner prints a summary and a machine-readable line per result:

```
CONFORMANCE workflow.v1 @ <contract_version> (oracle <oracle_commit>)
  bundle           18 pass   0 fail   0 skip
  canonicalization  2 pass   0 fail   0 skip
  compiler          0 pass   0 fail   0 skip   (no vectors — see below)
  executor         33 pass   0 fail   0 skip
  gate              4 pass   0 fail   0 skip
  schema            4 pass   0 fail   0 skip
  snippets         51 pass   0 fail   0 skip
  templates        79 pass   0 fail   0 skip
  TOTAL           191 pass   0 fail   0 skip
```

A family with **no vectors** prints its zero rather than being omitted. An absent family
reads as green to anyone scanning the report, and it is precisely where undetected drift
accumulates: `compiler` has none, so nothing today would catch the two engines serialising
numbers differently.

Vectors parked under a `_pending/` directory are excluded from the gate and **named in the
report**. Parking is the kit's escape hatch for a disagreement that needs a human decision;
letting the parked set vanish silently would be the same failure the hatch is meant to
survive.

Coverage per family (and per node type / IdempotencyKind / error code) is reported too — a
coverage hole is undetected future drift (kit spec §4.5).

## CI wiring

Both products run the same vectors against their own adapter. Which families BLOCK a merge is
a per-product decision recorded in that product's repository, not here — a gate that blocks on
a family known to diverge is red on its first run and switched off within a week, and a gate
that blocks on nothing is decoration. The kit's rule is only that the set is explicit and that
a family outside it still runs and still reports.

- **Nashira:** a required job; exit `1` fails the pipeline. Every family is currently green,
  so all of them block.
- **flow-weaver:** a required job whose blocking set starts at `canonicalization` and `schema`
  — the families whose behaviour is already established as equivalent — with the rest running
  and reporting until each is promoted by its own change.
