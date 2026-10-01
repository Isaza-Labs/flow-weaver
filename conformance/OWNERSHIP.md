# Who owns this kit

FlowWeaver maintains the `workflow.v1` conformance kit in this repository, under
`conformance/`. FlowWeaver is the contract's oracle; this directory is the authoritative copy.

## Proposing a change

A contract change is a pull request against this repository. It must:

1. Explain the change and the reasoning behind it in the pull request.
2. Be reified from the **oracle**: read FlowWeaver's handler or engine code, not this kit's
   prose. A vector or SPEC sentence written from the specification alone, without checking the
   implementation, is how a wrong assertion survives review.
3. Bump `contract_version` where the change requires it (see `README.md`, "Changing a vector =
   changing the contract").
4. Regenerate `PINNED` (digest and per-family counts) in the same change. The integrity check in
   `flow_weaver_backend.Tests/Conformance/KitIntegrityTests.cs` fails when the vectors or the
   schema do not match it.

Do not edit files in this directory to make a build pass. The kit is what tells you the build is
wrong.

## Using the kit from another implementation

Other implementations consume the kit; they do not maintain a fork of it.

- **Vendor a pinned copy.** Copy `schema/`, `vectors/`, `VERSION`, `PINNED` and the SPEC
  documents from a specific commit of this repository into your own build, unmodified.
- **Check the copy.** Recompute the digest and per-family counts recorded in `PINNED` (SHA-256
  over every vector and the schema, sorted by path; each file contributes its path relative to
  `conformance/`, a NUL byte, its content and a NUL byte) so any local edit
  fails your build, and so every green run is attributable to a specific contract version.
- **Update deliberately.** Move to a newer kit by re-copying it and updating your pin in one
  change, and treat any difference between your copy and the pinned commit as a defect.
- **Write an adapter**, not a patch to the kit. See `adapters/README.md` and
  `ci/run-conformance.md`.

If you believe the contract is wrong, open an issue or a pull request here rather than changing
your copy.
