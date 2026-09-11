# FlowWeaver quick start

This guide describes how to prepare and validate a repository distribution. Exact runtime commands must be confirmed in the executable repository before publication.

## 1. Prerequisites

- Git.
- Docker and Docker Compose for container-based deployment.
- The SDKs and runtimes pinned by the executable repository.
- A supported PostgreSQL instance.
- An approved inference endpoint when AI-assisted authoring is enabled.

## 2. Clone and inspect

```bash
git clone <REPOSITORY_URL> flowweaver
cd flowweaver
```

Before configuring the application, verify that these files exist at the root:

```text
LICENSE
NOTICE
README.md
QUICKSTART.md
THIRD-PARTY-NOTICES.md
```

Also confirm that `skills/`, `specs/`, `workflows/` and `contracts/workflow.v1/` retain their README files.

Review the complete target layout in [docs/repository/REPOSITORY_STRUCTURE.md](./docs/repository/REPOSITORY_STRUCTURE.md).

## 3. Configure safely

Copy the example environment file using the command appropriate to your operating system, then fill it locally. Never commit `.env`, credentials, private keys, customer data or production endpoints.

Validate every variable in `.env.example` against the actual application configuration. Remove placeholders before a release.

## 4. Build and test

Use the commands pinned in the executable repository's build files and CI workflow. A release candidate is acceptable only when:

- the backend and frontend build successfully;
- automated tests pass;
- database migrations are validated on a disposable instance;
- secret and dependency scans pass;
- the license and third-party notice checks pass;
- the SPDX/REUSE coverage check passes;
- the QA-to-production approval gate is exercised.

Do not copy speculative commands from an earlier staging document into the public guide.

## 5. Start a local environment

Use the repository's validated Compose or development profile. Record the real ports, health endpoints and initial-user procedure here only after a clean-machine test by someone other than the primary developer.

## 6. Distribution check

Follow [DISTRIBUTION_POLICY.md](./DISTRIBUTION_POLICY.md). Compare the release archive with its manifest, ensure no internal evidence or secrets are present and record the artifact hash.

## 7. Getting help

Use public issues for ordinary defects and feature requests. Use only the private channel identified in [SECURITY.md](./SECURITY.md) for vulnerabilities.
