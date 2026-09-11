# Distribution and release terms

## Source distribution

Every source release must include:

- the full Apache 2.0 text in `/LICENSE`;
- `/NOTICE`;
- verified third-party notices and license texts;
- source corresponding to the first-party release;
- build and dependency manifests needed for reproducibility;
- prominent notices on modified third-party files when required.

## Binary and container distribution

Binary archives and images must carry or provide a durable path to the same license and notice materials. The release review must cover content actually bundled, not only direct application dependencies.

## Release evidence

Keep outside the public repository:

- approved source baseline and commit;
- SBOMs for source, binaries and images;
- secret-scan and vulnerability-scan results;
- dependency and license decisions;
- release archive hashes;
- approvals and exception records;
- source offers or fulfillment evidence where applicable.

## Public repository gate

The public candidate must be built from an allowlist, independently scanned and compared with the private baseline. Internal history, legal evidence, raw QA traces and secrets are excluded. A release is not authorized merely because CI is green.

## Modification notices

When FlowWeaver distributes a modified third-party file under Apache 2.0, that file must carry a prominent statement that it was changed. Preserve all relevant upstream attribution, patent and trademark notices.

## Release approval

Engineering, security, license/compliance, IP ownership and release management approvals must be recorded. A single person may hold multiple roles in an early-stage company, but the record must identify which role was exercised.

