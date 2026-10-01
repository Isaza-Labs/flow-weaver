# workflow.v1

The `workflow.v1` contract is published as FlowWeaver's conformance kit in [`conformance/`](../../conformance/README.md). This directory holds no separate copy; everything below lives in the kit:

- machine-readable schema: `conformance/schema/workflow.v1.schema.json`;
- canonicalization rules: `conformance/canonicalization/SPEC.md`;
- the 1.1 layers (bundle, snippets, templates, execution): `conformance/*/SPEC.md`;
- valid and invalid examples and golden vectors: `conformance/vectors/` (including `vectors/schema/valid_*.json` and `invalid_*.json`);
- the runner contract and how to run the suite: `conformance/ci/run-conformance.md` and `conformance/adapters/README.md`;
- version and compatibility rules: `conformance/VERSION`, `conformance/PINNED` and "Changing a vector = changing the contract" in `conformance/README.md`.

The contract version is `1.1.0-draft`; see `conformance/VERSION` for its status.
