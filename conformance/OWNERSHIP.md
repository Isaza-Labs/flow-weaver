# Who owns this kit

**This directory is a copy.** An identical one lives at
`netora/workflow-v1-conformance/`. Neither is authoritative over the other, and that is a
problem with a shelf life, not a design.

## Why there are two

The `workflow.v1` contract governs two products: FlowWeaver, which is its oracle, and
Nashira, which implements it. The kit that defines the contract has to be readable and
runnable by both.

Today it is stored inside one of them. That means the maintainers of one product hold the
commit bit on the other product's contract — which is backwards even when the answer is
obvious, and unworkable the first time the two disagree about what the contract says.

A copy in each repository is the interim arrangement:

- Each build stays **hermetic**. Neither pipeline depends on another repository being
  reachable, and neither pulls an entire product to obtain a directory.
- `PINNED` records the `contract_version`, the `oracle_commit` the layers were reified
  from, a content digest and per-family counts, so any green run is attributable to a
  specific contract rather than to "the kit, whatever it said that day".
- An unreviewed local edit fails the build, because the digest stops matching.

## What the copy cannot do

**A copy cannot notice that its twin moved.** The digest catches tampering here; it says
nothing about a change made there. The two are kept together by review discipline and by
nothing else, which is exactly the property this kit exists to remove from the products
themselves.

So treat divergence between the two copies as a defect, not as a fork in progress.

## Editing it

Do not edit files in this directory to make a local build pass. That inverts the whole
arrangement: the kit is what tells you the build is wrong.

A genuine contract change is:

1. Made in **one** copy, with the reasoning in the change that carries it.
2. Reified from the **oracle** — read the handler, do not read this document's prose. Three
   assertions in this kit have been wrong precisely because they were written from the
   specification rather than from the code, and each survived review before anyone opened
   the implementation.
3. Copied verbatim into the other repository, in the same change or the one immediately
   after it.
4. Accompanied by a regenerated `PINNED` in each.

## Getting rid of the copies

The kit should leave both repositories. When it does:

1. **Create the repository.** `workflow-v1-conformance`, containing what this directory
   contains: `schema/`, `vectors/`, `VERSION`, and the SPEC documents (`bundle/`,
   `snippets/`, `templates/`, `execution/`, `canonicalization/`, `ci/`, `adapters/`).
   Carry the git history from `netora/workflow-v1-conformance/` if it can be preserved —
   the reasoning in those commits is most of the value.
2. **Give it a release.** A tag per `contract_version`, and a published artifact each
   consumer can pin: a NuGet package carrying the vectors as content is enough, and both
   products are .NET.
3. **Replace this directory with the dependency.** `PINNED` becomes the package version;
   the integrity test becomes redundant and should be deleted rather than left asserting a
   digest nobody regenerates.
4. **Move the ownership rule.** Write into the new repository who reviews a contract change
   and what "reified from the oracle" obliges them to check. That rule currently lives only
   in this file, in duplicate.
5. **Do steps 1-3 in both products in the same week.** Two consumers on different versions
   of the contract is the thing the extraction is meant to end, and it is easy to create
   accidentally by migrating one and getting distracted.

Until then: two copies, one digest each, and the awareness that they agree because someone
kept them agreeing.
