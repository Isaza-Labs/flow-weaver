# Required CI and release gates

## Pull-request checks

- Backend and frontend build.
- Automated tests and coverage threshold.
- Formatting and static analysis.
- Markdown and internal-link validation.
- Secret scanning.
- Dependency and vulnerability scanning.
- REUSE/SPDX licensing coverage.
- DCO sign-off.
- Contract/schema compatibility.
- Container lint and pinned action/image checks.

## Release checks

- SBOM for source, binaries and images.
- Third-party notice generation and review.
- Required source/license bundles for redistributed components.
- Clean-machine installation test.
- QA-to-production approval-gate exercise.
- Distribution allowlist comparison.
- Artifact hash and recorded approvals.

A reporting check is not a gate unless branch policy prevents merging or releasing when it fails.

