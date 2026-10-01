# Changelog

All notable changes to FlowWeaver are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and FlowWeaver uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning and support

`MAJOR.MINOR.PATCH`, where a **MAJOR** increment signals a change that requires action from an existing deployment. For FlowWeaver that specifically includes:

- a breaking change to the HTTP API or to the `workflow.v1` contract, of which FlowWeaver is the oracle;
- a migration that is not reversible, or that requires a documented manual step;
- a change to permission, RBAC, approval-gate or risk-classification semantics that alters what an existing configuration allows;
- a change to snippet execution or sandboxing that invalidates a workflow that previously ran;
- removal of a supported vendor, integration, handler or artifact format.

A change that tightens a security boundary can ship in a MINOR release when it does not require action from a correctly configured deployment. When it does require action, it is a MAJOR release and says so at the top of the entry.

Per [SECURITY.md](./SECURITY.md), the latest published release receives security fixes. The active development branch is not a supported production release.

Each entry should distinguish verified behavior from known limitations and breaking changes.

## Categories

`Added` · `Changed` · `Deprecated` · `Removed` · `Fixed` · `Security` · `Known limitations`

---

## [Unreleased]

### Added

- Public repository documentation package: project overview, quick start, governance, maintainers, contribution, support and security policies; the Apache 2.0 scope notices for skills, specifications and workflows; and the legal set (licensing framework, DCO, trademark policy, third-party notices control).

### Changed

- Updated the public entry points and quick start to describe the executable repository, AI-assisted draft creation, the visual editor, and current development checks.
- Corrected the historical third-party inventory; included verbatim package licence and agreement files for `khroma` 2.1.0, `LibGit2Sharp.NativeBinaries` 2.0.323, QuestPDF 2025.1.5, and the listed json-everything versions; and separated completed classification from release-specific work.

### Removed

- A development ASP.NET Data Protection key file that had been committed under `flow_weaver_backend/keyring/`. The `keyring/` directory is now git-ignored. Any deployment that used that key should generate a new keyring and re-enter its stored credentials (see [keyring rotation](./docs/ops/keyring-rotation.md)).
- The GitLab branch-protection guide and the internal QA-round issue template, which did not apply to this repository.

### Fixed

- Documentation cleanup across the repository, the in-app manual and the agent skills: removed internal references, corrected statements that contradicted the code, fixed broken links and replaced example data with documentation-reserved values.
- `.gitignore` no longer matches source folders named `Secrets` on case-insensitive filesystems.

### Known limitations

- **Pre-1.0 development state.** No stable release has been tagged; setup and interfaces may change. Verify a deployment in your own environment before using it for operational work.
- **The third-party notice is a historical inventory, not yet a complete release notice.** The [release work](./THIRD-PARTY-NOTICES.md#4-resolved-classifications-and-remaining-release-work) includes packaging third-party licence texts with the built artifacts, recording commercial-term decisions, and regenerating the inventory from those artifacts. The current public checkout has no committed NuGet lockfiles or `deploy/requirements.txt`, and its container bases use tags rather than digest pins.
- **No supported-version table exists** because no release has been tagged. Until then, fixes are applied to the current development line.

---

<!--
Template for a release entry:

## [1.2.0] - 2026-01-31

### Added
### Changed
### Fixed
### Security
### Known limitations

[1.2.0]: https://github.com/<org>/<repo>/compare/v1.1.0...v1.2.0
-->
