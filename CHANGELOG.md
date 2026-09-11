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

Each entry must distinguish, as [docs/governance/RELEASE_CHECKLIST.md](./docs/governance/RELEASE_CHECKLIST.md) requires, verified behavior from known limitations and breaking changes.

## Categories

`Added` · `Changed` · `Deprecated` · `Removed` · `Fixed` · `Security` · `Known limitations`

---

## [Unreleased]

### Added

- Public repository documentation package: project overview, quick start, governance, maintainers, contribution, support, security and incident-response policies; the Apache 2.0 scope notices for skills, specifications and workflows; and the legal set (licensing framework, DCO, trademark policy, third-party notices control).
- Reproducible dependency pinning: base images by `sha256` digest, a hash-verified Python requirement set for the backend image, and NuGet lock files restored in locked mode.
- A CycloneDX 1.6 SBOM covering the NuGet, npm, PyPI, operating-system and container layers.

### Known limitations

- **This package is not yet merged with the executable repository.** Paths, commands and ports cited in the documentation must be confirmed against the code before a public release. See [docs/governance/MERGE_CHECKLIST.md](./docs/governance/MERGE_CHECKLIST.md).
- **The third-party notice is an inventory, not yet a complete release notice.** The licence texts that must accompany the distribution, the libgit2 linking exception and the commercial decisions recorded in [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md) §4 are still open.
- **No supported-version table exists** because no release has been tagged. Until then, fixes are applied to the current development line.
- **The `workflow.v1` conformance kit is duplicated** between this repository and the Nashira repository by design, as an interim arrangement. Neither copy is authoritative over the other, and a copy cannot detect that its twin changed.

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
