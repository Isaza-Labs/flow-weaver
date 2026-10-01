# workflow.v1 conformance kit

FlowWeaver's **behavioral contract** for `workflow.v1`, as a versioned artifact and single
source of truth. This is **not the engine** — it is the verifiable definition of *how any
implementation of the engine must behave*.

FlowWeaver is the reference implementation (the **oracle**). Other implementations consume
this kit at a **pinned version** and run its schema, canonicalization rules and **golden
vectors** against their own engine through an adapter (`adapters/README.md`). Passing the
vectors is what turns "another implementation" into "a demonstrably equivalent
implementation". See `OWNERSHIP.md` for how the kit is maintained and how to vendor it.

## Layout

```
conformance/
├── README.md                     # this file
├── OWNERSHIP.md                  # who maintains the kit, how to change it, how to vendor it
├── VERSION                       # contract version and status
├── PINNED                        # contract version, content digest and per-family vector counts
├── schema/
│   ├── README.md
│   └── workflow.v1.schema.json   # structural JSON Schema (reified from FlowWeaver)
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
│   ├── README.md                 # vector anatomy, where an `expected` comes from, coverage
│   ├── schema/                   # family 1: structural validity
│   ├── canonicalization/         # family 2: canonical form + SchemaHash relations
│   ├── compiler/                 # family 3: workflow.v1 -> compiled plan (no vectors)
│   ├── executor/                 # family 4: execution behavior
│   ├── gate/                     # family 5: promotion gate
│   ├── bundle/                   # family 6 (1.1): bundle import outcomes + round trip
│   ├── snippets/                 # family 7 (1.1): per-type input normalization + portable output
│   └── templates/                # family 8 (1.1): template resolution + condition evaluation
├── adapters/
│   └── README.md                 # how an implementation wires its engine to the vectors
└── ci/
    └── run-conformance.md        # runner contract (equivalence modes, exit codes, report) and how to run it
```

## What is contract (and what is not)

**Contract** (goes in the kit): the JSON Schema of `workflow.v1`; the canonicalization spec
that feeds `SchemaHash`; the observable behavior of the compiler + executor (inputs → outputs,
idempotency, DAG order, rollback analysis, error taxonomy, **audit event shape**); the gate
state machine + codes; the operation-level `PermissionClassifier` taxonomy.

**Contract since 1.1** (the layers a *portable* workflow depends on, above the graph): the
interchange bundle (`bundle/SPEC.md`) — one portable identity per referenced thing,
`requires`, sub-workflows and triggers travelling, secrets never; the per-type snippet contract
(`snippets/SPEC.md`) — canonical input keys with FlowWeaver as the oracle, mandatory aliases,
portable output; the template profile (`templates/SPEC.md`) — namespaces, filters, per-device
scoping, condition grammar; and execution semantics (`execution/SPEC.md`) — retry shape,
subflow, what an importer does with triggers. Agreeing on the graph alone is not enough for two
engines to run each other's workflows; every one of these layers has to agree as well.

**Not contract** (never enters the kit): internal code structure, class names, service
architecture, language (C#/.NET), or any implementation detail. The golden rule: the contract
describes **externally observable behavior**, not how it is achieved inside.

## Oracle

`workflow.v1` is reified from FlowWeaver (this repository): the schema is extracted from its
workflow model, `canonicalization/SPEC.md` is written from its real behavior, and each vector is
written from the SPEC text, consulting FlowWeaver's code where the text is silent or ambiguous
(`vectors/README.md`). FlowWeaver runs its own conformance suite: being the oracle is the
reason for the check, not an exemption from it. For any other implementation, the acceptance
criterion for its engine is *"done = passes workflow.v1 conformance vN"*.

## Enforcement

Each implementation runs every vector against its own adapter and declares which families
**block a merge**; families outside that set still run and still report
(`ci/run-conformance.md`).

This repository has no CI pipeline. Run the suite locally with:

```
dotnet test flow_weaver_backend.Tests/flow_weaver_backend.Tests.csproj --filter "FullyQualifiedName~flow_weaver_backend.Tests.Conformance"
```

The suite is intended to gate merges once CI is configured. FlowWeaver's blocking set
(`canonicalization`, `gate`, `schema`, `templates`) and its per-family pass floors are declared
in `flow_weaver_backend.Tests/Conformance/ConformanceGateTests.cs`.

## Changing a vector = changing the contract

A PR that adds/modifies/removes a vector is a **contract change**, not a test change: it needs a
written rationale, a matching semver bump, a regenerated `PINNED`, and proof that the oracle
still complies. Never auto-regenerate goldens to turn a red green — that erases the contract.
See `vectors/README.md` ("Where an `expected` comes from") for how to decide which side is wrong
when a vector fails, and when to park it under `_pending/`.
