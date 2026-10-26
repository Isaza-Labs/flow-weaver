# CI and release gates

A reporting check is not a gate unless branch policy prevents merging or releasing when it fails. This page says which checks run, which of them block a merge, and what is still done by hand.

## Pull-request checks (`.github/workflows/ci.yml`)

CI runs on every pull request to `main`, on every push to `main` and on manual dispatch. It has read-only repository permissions, and a newer push to the same branch cancels the run in progress.

| Job (required check) | Runner | What it runs | Where to read a failure |
|---|---|---|---|
| `backend` | Ubuntu | `dotnet restore`, `build` and `test` for `flow_weaver_backend.Tests`, which includes the conformance gate, the conformance kit integrity check and the import test for `examples/` bundles | Job log, and the `backend-test-results` artifact (TRX file and `conformance-summary.txt`) |
| `frontend` | Ubuntu | `npm ci`, `npm run check`, `check:hints`, `check:labels` and `build`, on the Node version in `frontend/.nvmrc` (22, the same major as `deploy/Dockerfile.frontend`) | Job log; on failure, the `frontend-check-logs` artifact holds each check's full output |
| `repository` | Windows | `node tools/check-doc-links.mjs` (relative links in Markdown, case-sensitive), and the conformance kit integrity test on a checkout made with `core.autocrlf=true` | Job log |

The `repository` job runs on Windows on purpose. The kit integrity test already runs on Linux inside `backend`; on Windows it shows that the digest does not depend on how the checkout converts line endings (see [conformance/OWNERSHIP.md](../../conformance/OWNERSHIP.md)). External links are not fetched, so a third-party site that is briefly down cannot block a pull request.

The job names are the required status checks on `main`. Renaming a job removes it from branch protection without warning, so a rename must update this page and the branch rule in the same change.

## Branch protection for `main`

The rule for `main` requires:

- changes through a pull request;
- the `backend`, `frontend` and `repository` checks to pass, on a branch that is up to date with `main`;
- all review conversations resolved before merging;
- no force pushes and no deletion of `main`.

**Approvals.** The project currently has a single active maintainer, so the rule requires **zero** approving reviews. Requiring a second reviewer who does not exist would make every merge a bypass, and a routine bypass is no longer a control. The pull request and the required checks remain mandatory. Raise the approval count to one when a second maintainer is active.

**Administrative bypass.** Only repository administrators may bypass the rule, and only to restore service when CI itself is broken (for example, a runner image change that fails every job). Each bypass is recorded in the pull request with the reason and is followed by a pull request that makes CI green again. A failing test is never a reason to bypass.

## Dependency advisories (`.github/workflows/dependency-audit.yml`)

The audit runs `npm audit` for `frontend/` and `dotnet list package --vulnerable --include-transitive` for the backend and test projects. `tools/dependency-audit-report.mjs` turns both into one table.

| Trigger | Result |
|---|---|
| Pull request or push that changes `package.json`, `package-lock.json`, a `.csproj` or a NuGet lockfile | Fails on any **high** or **critical** advisory. The full table is in the job summary and in the `dependency-audit` artifact. |
| Every Monday at 06:17 UTC, and manual dispatch | Keeps **one** open issue, "Dependency audit: open advisories" (labels `dependencies`, `needs-triage`), up to date with every **moderate** or higher advisory, and closes it when none remain. |

The audit is not a required check, because a path-filtered workflow does not run on every pull request. A new advisory that appears between dependency changes reaches the maintainers through the weekly issue instead.

Each entry in the issue needs a recorded outcome, as a comment on the issue or in the pull request that resolves it:

- **Fixed:** the dependency was updated or replaced. Name the version.
- **Evaluated:** the package is affected but FlowWeaver is not exposed. Give the reason (for example, build-time only, or the vulnerable function is never called with untrusted input) and what would change that assessment. Being in `devDependencies` is not a reason by itself: check whether the package ships in an image.

Advisories are not suppressed in the tooling. `npm audit fix --force` is not used: a major upgrade is made deliberately, with its breaking changes reviewed.

## Not yet automated

These are still checked by hand, or not at all, and are not gates yet:

- formatting and static analysis beyond `svelte-check` and the compiler;
- coverage thresholds;
- secret scanning (enable GitHub secret scanning and push protection in the repository settings);
- REUSE/SPDX licensing coverage and DCO sign-off;
- container lint, and pinning of actions and base images by digest.

## Release checks

- SBOM for source, binaries and images.
- Third-party notice generation and review.
- Required source/license bundles for redistributed components.
- Clean-machine installation test.
- QA-to-production approval-gate exercise.
- Distribution allowlist comparison.
- Artifact hash and recorded approvals.

These are release-time checks performed by the maintainer. The release notes state which of them were completed for that release.
