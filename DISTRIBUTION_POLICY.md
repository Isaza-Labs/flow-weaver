# Repository and distribution policy

## Purpose

This file protects the expected public structure of FlowWeaver. A release or repository initialization that removes required directories, licensing files or ownership notices fails the distribution check.

## Required root files

- `README.md`
- `QUICKSTART.md`
- `LICENSE`
- `NOTICE`
- `THIRD-PARTY-NOTICES.md`
- `CONTRIBUTING.md`
- `SECURITY.md`
- `CODE_OF_CONDUCT.md`
- `TRADEMARK.md`
- `LEGAL.md`
- `GOVERNANCE.md`
- `REUSE.toml`

## Required directories

- `flow_weaver_backend/`
- `flow_weaver_backend.Tests/`
- `frontend/`
- `deploy/`
- `docs/`
- `assets/`
- `examples/`
- `tools/`
- `legal/`
- `compliance/`
- `LICENSES/`
- `skills/`
- `specs/`
- `workflows/`
- `contracts/workflow.v1/`
- `.github/ISSUE_TEMPLATE/`

Every principal directory must contain a README describing purpose, permitted contents, validation and ownership. A README is preferred over a placeholder-only `.gitkeep` because it preserves intent.

## Licensing rules

1. The root `LICENSE` is the sole first-party project license: Apache License 2.0.
2. Do not add a competing first-party license per directory. A verbatim Apache 2.0 copy may remain only when that directory is distributed independently; otherwise the root license is sufficient.
3. Preserve third-party license texts and attribution notices.
4. Regenerate or verify `THIRD-PARTY-NOTICES.md` for every release.
5. Modified third-party files must carry the notices required by their original license.

## Release exclusions

Do not publish:

- `.env`, secrets, tokens or private keys;
- customer or employee personal data;
- internal legal evidence, draft assignments or identity documents;
- raw QA traces containing sensitive data;
- repository bundles or internal commit-identity mappings;
- temporary files, local build caches or unreviewed generated artifacts.

## Release gate

- [ ] Required files and directories exist.
- [ ] All READMEs and links resolve.
- [ ] Apache 2.0 is the only declared first-party license.
- [ ] `NOTICE` names Isaza Labs LLC.
- [ ] Third-party notices match the dependency lockfiles and images.
- [ ] SPDX/REUSE coverage passes for first-party files and third-party overrides are present.
- [ ] The asset manifest covers all published media and brand files.
- [ ] No placeholder contacts or repository URLs remain.
- [ ] Builds, tests and security scans pass.
- [ ] The release archive is inspected and its SHA-256 is recorded.
