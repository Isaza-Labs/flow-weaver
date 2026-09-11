# FlowWeaver

FlowWeaver is a governed, multi-vendor network automation platform. AI can help design a workflow, but a deterministic engine executes the typed artifact through draft, verified QA and production approval stages.

**Owner and steward:** Isaza Labs LLC  
**License:** [Apache License 2.0](./LICENSE)  
**Project status:** pre-1.0; interfaces may change before the first stable release

## Start here

- [Quick start](./QUICKSTART.md)
- [Repository and distribution policy](./DISTRIBUTION_POLICY.md)
- [Recommended repository structure](./docs/repository/REPOSITORY_STRUCTURE.md)
- [Exact public repository boundary](./PUBLIC_REPOSITORY_CONTENTS.md)
- [Legal and licensing index](./LEGAL.md)
- [Project governance](./GOVERNANCE.md)
- [Contribution guide](./CONTRIBUTING.md)
- [Security policy](./SECURITY.md)
- [Third-party notices](./THIRD-PARTY-NOTICES.md)
- [Trademark policy](./TRADEMARK.md)

## What is included

- A canonical `workflow.v1` model.
- A deterministic and idempotent execution engine.
- A verified simulation gate bound to the workflow content.
- Human approval before production execution.
- Multi-vendor specs and reusable workflows.
- RBAC, auditability and self-hosted deployment.

## Repository map

```text
/
├── flow_weaver_backend/        Backend and execution engine
├── flow_weaver_backend.Tests/  Automated tests
├── frontend/                   Visual builder and user interface
├── deploy/                     Deployment assets
├── docs/                       Product and operator documentation
├── assets/                     Approved visual and brand assets
├── examples/                   Safe, synthetic examples
├── tools/                      Developer and release automation
├── skills/                     Reusable agent skills
├── specs/                      Integration and vendor specifications
├── workflows/                  Reusable workflow artifacts
├── contracts/                  Versioned technical contracts
│   └── workflow.v1/
├── .github/                    GitHub workflows and issue templates
├── legal/                      Public licensing and contribution policies
├── compliance/                 Public release-compliance templates
├── LICENSES/                   SPDX/REUSE license texts
├── LICENSE                     Apache License 2.0
├── NOTICE                      Project attribution
└── THIRD-PARTY-NOTICES.md      Third-party attribution control
```

The directories listed above form the expected public distribution. Each principal directory carries a README explaining its purpose and acceptance rules. Empty ecosystem directories must retain their README so the intended layout is not lost.

## Licensing

All first-party FlowWeaver code and documentation in the distribution are licensed under Apache License 2.0. Components supplied by third parties remain under their original licenses; their notices must be preserved in `THIRD-PARTY-NOTICES.md` and in release artifacts where required.

The element-by-element rules are in [legal/LICENSING_FRAMEWORK.md](./legal/LICENSING_FRAMEWORK.md). SPDX/REUSE metadata in [`REUSE.toml`](./REUSE.toml) provides machine-readable coverage for known first-party paths.

Apache 2.0 does not grant rights to use the FlowWeaver name or logo except for reasonable identification of the project. See [TRADEMARK.md](./TRADEMARK.md).

## Contributions

Contributions are accepted under Apache 2.0 and require a DCO sign-off. See [CONTRIBUTING.md](./CONTRIBUTING.md).

## Repository history

The publicable repository may start from a clean history. Original development history and authorship evidence are preserved separately in the internal repository and its verified bundle. Commit metadata in the clean repository must not be used as the sole evidence of original authorship.

## Current limitations

This package defines the repository-level files and directory contracts that must be merged with the executable source tree. Commands, ports and deployment variables must be validated against that source before the repository is made public. Do not present placeholder commands as tested behavior.

## Project governance

FlowWeaver is owned and stewarded by **Isaza Labs LLC**. The current licensing and ownership decision is recorded in `03_Gestion/FlowWeaver_Decision_Licencia_Titularidad_2026-08-14.md` in the project dossier.
