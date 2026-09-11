# Branch protection — GitLab

Configuration lives under **Settings → Repository** in the GitLab project.

## Protected branches

`Settings → Repository → Protected branches`

| Branch | Allowed to push | Allowed to merge | Force push | Code owner approval |
|---|---|---|---|---|
| `main` | No one | Maintainers | No | Required |
| `dev`  | Maintainers | Developers + Maintainers | No | Optional |

## Merge request approvals

`Settings → Merge requests → Merge request approvals`

| Setting | Value |
|---|---|
| Approvals required for `main` | 1 |
| Prevent approval by author | yes |
| Prevent approval by committers | yes |
| Reset approvals when new commits are pushed | yes |
| Require an approval from someone who has not contributed to the MR | yes (if team size allows) |

## Merge checks

`Settings → Merge requests → Merge checks`

| Setting | Value |
|---|---|
| Pipelines must succeed | yes |
| Skipped pipelines are considered successful | no |
| All threads must be resolved | yes |
| Pipeline must run for the latest commit | yes |

## Required pipeline jobs

The pipeline gate is the `ci-gate` job at the end of `.gitlab-ci.yml`.
GitLab's "Pipelines must succeed" check fails the merge if any required
job in the pipeline fails. The required jobs (declared via `needs:` on
`ci-gate`) are:

- `backend-build`
- `backend-test`
- `frontend-lint`

`trivy-fs` is `allow_failure: true` today — high/critical findings show
in the pipeline summary but do not block the merge.

## Coverage gate

`backend-test` enforces line coverage ≥ 60 % via the `COVERAGE_THRESHOLD`
CI variable. Override per-pipeline by setting it manually under
`Settings → CI/CD → Variables`.

The coverage badge in the README parses the `coverage:` regex on the
`backend-test` job. Configure the project to use the pipeline as its
coverage source under
**Settings → CI/CD → General pipelines → Test coverage parsing** =
`Cobertura - GitLab parses the coverage`.

Raise the threshold in 5 % increments once the suite stabilises above
the floor. Lowering requires a recorded reason in the MR description.

## Manual override

A maintainer may force-merge a failing MR via **Merge → Skip merge
checks** only when:

- the failure is in an external service (Trivy DB outage, GitLab.com
  outage, container registry hiccup), and
- the reason is recorded in the MR description.

Force-merge events show up in the project audit log
(**Secure → Audit events**).

## Project CI/CD variables

| Variable | Scope | Why |
|---|---|---|
| `JWT_KEY` | Protected, masked | Used by deployment pipelines (when added). The application boot rejects the placeholder default outside Development. |
| `POSTGRES_PASSWORD` | Protected, masked | Used by deployment pipelines. The CI test job uses an inline `test` password against the ephemeral postgres service. |
| `KMS_*` | Protected, masked | Reserved for the planned DataProtection KMS integration (see `docs/ops/keyring-rotation.md`). |
