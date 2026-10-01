# Recommended FlowWeaver repository structure

## Design principles

- Preserve the current .NET and frontend paths to avoid a disruptive move into a new `src/` hierarchy.
- Keep product code, ecosystem artifacts, public documentation and release compliance distinguishable.
- Keep internal legal evidence and historical repository bundles outside the public repository.
- Give each durable directory a README that states purpose, accepted contents and validation.

## Illustrative target tree

This tree is a planning reference, not an inventory of the current checkout.
For paths that exist today, see the [root README](../../README.md).

```text
flowweaver/
├── .github/
│   ├── ISSUE_TEMPLATE/
│   ├── PULL_REQUEST_TEMPLATE/
│   ├── workflows/
│   └── CODEOWNERS
├── assets/
│   ├── brand/
│   └── ASSET_MANIFEST.csv
├── compliance/
│   ├── templates/
│   └── README.md
├── contracts/
│   └── workflow.v1/
├── deploy/
├── docs/
│   ├── architecture/
│   ├── operations/
│   ├── security/
│   └── repository/
├── examples/
├── flow_weaver_backend/
├── flow_weaver_backend.Tests/
├── frontend/
├── legal/
├── LICENSES/
│   └── Apache-2.0.txt
├── skills/
├── specs/
├── tools/
├── workflows/
├── CONTRIBUTING.md
├── DISTRIBUTION_POLICY.md
├── GOVERNANCE.md
├── LEGAL.md
├── LICENSE
├── NOTICE
├── QUICK_START_GUIDE.md
├── README.md
├── REUSE.toml
├── SECURITY.md
└── THIRD-PARTY-NOTICES.md
```

## Directory responsibilities

| Path | Responsibility | Must not contain |
|---|---|---|
| `flow_weaver_backend/` | Backend, domain and execution engine | Secrets, customer exports |
| `flow_weaver_backend.Tests/` | Automated tests and synthetic fixtures | Production credentials or real personal data |
| `frontend/` | Visual builder and web client | Server secrets |
| `deploy/` | Images, Compose, installation and operations assets | Unpinned downloads or embedded tokens |
| `skills/` | Reusable agent skills | Undocumented permissions or credentials |
| `specs/` | Integration/vendor contracts | Copied proprietary vendor content without permission |
| `workflows/` | Reusable workflow artifacts | Real infrastructure identifiers |
| `contracts/` | Versioned schemas and conformance material | Unversioned breaking changes |
| `docs/` | Public product and operator documentation | Private legal evidence or internal-only URLs |
| `assets/` | Approved visual/media files | Unverified downloads |
| `examples/` | Safe tutorials and synthetic examples | Customer data |
| `tools/` | Developer and release automation | Unreviewed destructive scripts |
| `legal/` | Public licensing and contribution policies | Signed assignments, IDs, cap tables |
| `compliance/` | Public templates and release metadata | Raw confidential audit evidence |

## Kept outside the repository

Corporate and legal records, vulnerability details, test traces with infrastructure data, release approvals and secret-scan evidence are kept by Isaza Labs LLC outside the public Git tree. See [PUBLIC_REPOSITORY_CONTENTS.md](../../PUBLIC_REPOSITORY_CONTENTS.md).

