# workflow.v1 conformance kit

The **shared behavioral contract** between flow-weaver (FW) and Nashira, as a versioned
artifact and single source of truth. This is **not the engine** — it is the verifiable
definition of *how any implementation of the canonical engine must behave*.

FW and Nashira have **separate engine implementations** (independent repos, copy-and-adapt —
see `nashira_requerimientos_v2.md §1.5.1 / §1.5.7`). The only thing that keeps them from
drifting is this kit: schema + canonicalization + **golden vectors** that both consume at a
**pinned version** and run against in CI. A red conformance test **blocks the merge**. That
turns "two implementations" into "two demonstrably equivalent implementations".

## Layout

```
workflow-v1-conformance/
├── VERSION                       # contract version + frozen oracle commit
├── schema/
│   └── workflow.v1.schema.json   # structural JSON Schema (reified from FW)
├── canonicalization/
│   └── SPEC.md                   # normative canonicalization algorithm (feeds SchemaHash)
├── bundle/
│   └── SPEC.md                   # 1.1: the interchange bundle (v3) — portable identity, requires, subflows, triggers
├── snippets/
│   └── SPEC.md                   # 1.1: per-type input keys, aliases, portable output, idempotency floor
├── templates/
│   └── SPEC.md                   # 1.1: template namespaces, filters, per-device scoping, condition grammar
├── execution/
│   └── SPEC.md                   # 1.1: outcomes, retry, subflow, triggers-on-import, audit.v1
├── vectors/                      # pure JSON, language-agnostic (input -> expected + equivalence)
│   ├── schema/                   # family 1: structural validity
│   ├── canonicalization/         # family 2: canonical form + SchemaHash relations
│   ├── compiler/                 # family 3: workflow.v1 -> compiled plan
│   ├── executor/                 # family 4: execution behavior
│   ├── gate/                     # family 5: promotion gate + permission classification
│   ├── bundle/                   # family 6 (1.1): bundle import outcomes + round trip
│   ├── snippets/                 # family 7 (1.1): per-type input normalization + portable output
│   └── templates/                # family 8 (1.1): template resolution + condition evaluation
├── adapters/
│   └── README.md                 # how each repo wires its engine to the vectors
└── ci/
    └── run-conformance.md        # runner contract (exit codes, report)
```

## What is contract (and what is not)

**Contract** (goes in the kit): the JSON Schema of `workflow.v1`; the canonicalization spec
that feeds `SchemaHash`; the observable behavior of the compiler + executor (inputs → outputs,
idempotency, DAG order, rollback analysis, error taxonomy, **audit event shape**); the gate
state machine + codes; the operation-level `PermissionClassifier` taxonomy.

**Contract since 1.1** (2026-08-27 — the layers a *portable* workflow depends on, above the
graph): the interchange bundle (`bundle/SPEC.md`) — one portable identity per referenced thing,
`requires`, sub-workflows and triggers travelling, secrets never; the per-type snippet contract
(`snippets/SPEC.md`) — canonical input keys with FW as the oracle, mandatory aliases, portable
output; the template profile (`templates/SPEC.md`) — namespaces, filters, per-device scoping,
condition grammar; and execution semantics (`execution/SPEC.md`) — retry shape, subflow, what an
importer does with triggers. Rationale: both products already agreed on the graph and still
could not run each other's workflows, because every one of these layers had drifted.

**Not contract** (never enters the kit): internal code structure, class names, service
architecture, language (C#/.NET), or any implementation detail. The golden rule: the contract
describes **externally observable behavior**, not how it is achieved inside.

## Oracle + bootstrap

`v1` is reified from FW at a frozen `oracle_commit` (see `VERSION`): extract the schema, write
`canonicalization/SPEC.md` from FW's real behavior, and generate the golden vectors by capturing
FW's outputs. Vectors are then hand-reviewed (a captured output can reflect an oracle bug). FW
must pass its own conformance as a sanity check. Nashira is the first **native** implementation:
its engine acceptance criterion is *"done = passes workflow.v1 conformance vN"*.

## Enforcement

- **Nashira:** conformance runs in CI against the pinned kit version; a red vector **blocks the
  merge** (hard gate). Without the block, the kit is decoration.
- **flow-weaver (initial):** a **read-only** check — runs the vectors and *reports* divergence
  without blocking, as an early one-sided drift alarm. Promoted to a block as adoption allows.

## Changing a vector = changing the contract

A PR that adds/modifies/removes a vector is a **contract change**, not a test change: it needs a
written rationale, a matching semver bump, and proof both products (or the oracle, per phase)
still comply. Never auto-regenerate goldens in CI to turn a red green — that erases the contract.
See the kit spec (`../workflow_v1_kit_conformidad.md`) §4.3–§4.4 for the failure-triage buckets.
