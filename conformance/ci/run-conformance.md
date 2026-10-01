# Conformance runner contract

The runner is the harness each implementation ships to check its engine against the pinned kit.
The kit defines the **contract** the runner must honor; the runner itself lives in each
implementation's repository (it is language-specific, like the adapter). FlowWeaver's runner is
`flow_weaver_backend.Tests/Conformance/ConformanceRunner.cs`, driven by the xUnit test
`ConformanceGateTests.cs` in the same folder.

## Inputs

- The pinned kit version (this directory), specifically `vectors/**/*.json` and
  `schema/workflow.v1.schema.json`.
- The implementation's **adapter** (see `../adapters/README.md`), which maps a vector's `input`
  to its engine and normalizes the engine's output to the vector's `expected` shape.

## Per-vector algorithm

For each vector file:

1. Read the vector (`id`, `family`, `input`, `expected`, `equivalence`, optional `normalize`).
2. Dispatch `input` to the adapter method for `family`.
   - An adapter that cannot answer the family **FAILS** the vector. Silence is not a skip: an
     adapter that quietly returned nothing would let a family with no implementation behind it
     report green, which is the failure this kit exists to make impossible.
   - The **only** legitimate skip is a vector that declares `"not_implemented": true` about
     itself. A skip is a statement the CONTRACT makes about what is not required, never a
     statement an implementation makes about what it did not get around to.
3. Apply the vector's `normalize` rules (below) to both actual and expected.
4. Compare actual vs expected using the vector's `equivalence` mode (below).
5. Record `pass` / `fail` / `skip` with the vector id and, on failure, the normalized diff.

## Equivalence modes

| Mode | Compares |
|---|---|
| `exact` | Literal equality (of the canonical JSON form) after normalization |
| `normalized` | `exact` after the vector's `normalize` rules (timestamps/ids redacted) |
| `subset` | Every member `expected` names is present and equal in actual; extra members in actual are ignored |
| `fields-present` | Presence + JSON type of the fields `expected` names, not their values; a `null` in `expected` asserts presence only |
| `yaml-normalized` | YAML equality, map key order irrelevant, lists ordered |
| `set-equal:<field>` | Set equality of `<field>` (order irrelevant) |
| `partial-order` | Order constraints only (e.g. a before c) |
| `numeric-tolerance:<eps>` | Equality within tolerance |

The landed vectors use `exact`, `subset` and `fields-present` only.

Choosing the wrong mode is the most common error: `exact` over output with timestamps yields
false reds; `set-equal` where order matters (DAG execution) hides real bugs. The mode is a
**contract decision**, reviewed per vector.

## Normalize rules

| Rule | Meaning |
|---|---|
| `redact:<field>` | Every occurrence of `<field>`, at any depth, becomes `"<redacted>"` in **both** expected and actual. The field must still be present — redacting is not ignoring. |
| `sort:<field>` | The array at `<field>`, at any depth, is sorted by each element's canonical form. For collections the contract does not order. |

A runner that meets a rule it does not implement must fail rather than ignore it: a rule nobody
implements is a comparison nobody is really making.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | All non-skipped vectors passed |
| `1` | One or more vectors failed (**blocks the merge** where the suite gates merges) |
| `2` | Runner/setup error (bad kit, unreadable vector, adapter crash) |

FlowWeaver's runner is an xUnit test, so it reports through `dotnet test`: the run fails when a
family in its blocking set has a failing vector or when a family drops below its pass floor.

## Report

The runner prints a summary and a line per result. For an implementation that passes every
vector it looks like this:

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
accumulates: `compiler` has none, so no vector would catch two engines serialising numbers
differently.

Vectors parked under a `_pending/` directory are excluded from the gate and **named in the
report**. Parking is the kit's escape hatch for a disagreement that needs a human decision;
letting the parked set vanish silently would be the same failure the hatch is meant to
survive.

Pass counts are reported per family — a coverage hole is undetected future drift.

FlowWeaver's summary also marks each family `REQUIRED` or `reporting` and groups failures by
reason. It is printed in the test output and written to `TestResults/conformance-summary.txt`
at the repository root.

## Running it and gating merges

Every implementation runs the same vectors against its own adapter. Which families BLOCK a
merge is a per-implementation decision recorded in that implementation's repository, not
here — a gate that blocks on a family known to diverge is red on its first run and quickly
switched off, and a gate that blocks on nothing is decoration. The kit's rule is only that the
set is explicit and that a family outside it still runs and still reports.

This repository has no CI pipeline. Run FlowWeaver's suite locally with:

```
dotnet test flow_weaver_backend.Tests/flow_weaver_backend.Tests.csproj --filter "FullyQualifiedName~flow_weaver_backend.Tests.Conformance"
```

The suite is intended to gate merges once CI is configured. Its blocking set is
`canonicalization`, `gate`, `schema` and `templates`; the other families run and report, held
by per-family pass floors. Both are declared in
`flow_weaver_backend.Tests/Conformance/ConformanceGateTests.cs`.
