# CI and release gates

The public repository does not include CI configuration. The checks below are the gates a maintainer CI and branch policy should enforce. Until they are configured, contributors run the build and test commands in the [quick start](../../QUICK_START_GUIDE.md#6-tests) and report the results in the pull request.

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

