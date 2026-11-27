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

## Unreleased

### Added

- NetBox inventory sync, ported from Nashira. An inventory source of type `netbox` takes `config.base_url`, an optional `config.token_secret_ref`, `config.site_filter` and `config.allow_private_network`. `POST /api/inventorysource/{id}/sync` reads `/api/dcim/devices/` page by page and upserts devices. It matches them by NetBox id, then by name, which adopts manual devices. It follows renames, keeps credentials, environments and SSH fingerprints set in FlowWeaver, and never deletes. `?dryRun=true` reports created, updated, unchanged and skipped counts without writing. The outcome is recorded in `last_sync_at` / `last_sync_status` and in the audit log. Syncing needs `inventory.manage`.
- The token is never stored in the source: `token_secret_ref` is a `${secret:...}` reference, for example `${secret:integration:<id>:token}` to reuse a NetBox integration's token, or a stored secret's name. Raw tokens are rejected and a legacy literal one is returned as `***`. v2 tokens (`nbt_…`) are sent as `Bearer`, v1 tokens as `Token`.
- New **Inventory sources** page (`/inventory`, under Operate) to create and edit NetBox sources, run a dry run or a sync, and see the last result.
- Versioned Python environments built with uv. paramiko 5.0 removed the `ssh-rsa` host-key algorithm, and the image installs `paramiko>=3` unbounded, so a device whose sshd only offers `ssh-rsa` (seen on Extreme EXOS with OpenSSH_8.1) failed with `Incompatible ssh peer (no acceptable host key)`. Instead of downgrading paramiko for everyone, an admin can now define environments, each with its own pinned packages, under **Admin → Python environments** (`/admin/python-environments`, API `/api/python-environments`). The worker builds them at runtime with uv into `/app/pyenv/envs/<name>`, with no image rebuild. It swaps the finished build in atomically, keeps the previous good build when a build fails, and records `uv pip freeze`. Editing the requirements or the Python version, or pressing Rebuild, rebuilds the environment. The image ships uv 0.13.8. Its cache lives on the shared `python_packages` volume.
- A device's SSH runner, a `python_snippet` and an `ansible_playbook` snippet can each choose an environment. With no environment they keep using the image's python3 and ansible, as before. A Python snippet keeps every sandbox protection under an environment: the import allow-list, the bwrap isolation, prlimit, the timeouts and the network rules. The environment directory is bound read-only.
- On startup, a deployment with no environments (including an upgraded one) is seeded with `ssh-legacy` (`netmiko>=4,<5`, `paramiko>=3.5,<5`, `textfsm>=1.1`, `ntc-templates>=4`), flagged as the SSH fallback. When an ssh step fails with the paramiko host-key negotiation error, it is retried once with the ready fallback environment. The step output then reports `ssh_env_fallback: true` with the original error. Every ssh step output now names the environment it ran under in `python_environment` (`system` when none).
- Writes are admin-only, because installing packages runs third-party code. Anyone who can manage devices or snippets can read the list for the pickers, and the new `pythonenv.read` capability also grants it. Credentials in index URLs are masked in API responses and in the audit log.
- The ssh device type alias `exos` (also `extreme`) now maps to netmiko's `extreme_exos`.

### Fixed

- The SSH runner no longer fails for every private-key credential on paramiko 4.0 or later. It referenced `paramiko.DSSKey`, which paramiko 4 removed, so it raised an `AttributeError`. The runner now builds the list of key classes from those the installed paramiko still has.

### Security

- Setting a source's token reference, or changing its `base_url` while one is set, requires an admin. `inventory.manage` is an Operator capability, and the sync sends the resolved secret to `base_url`. Without this rule an Operator could point a source at their own host and read any stored secret.

### Changed

- The `workflow_run` rate limit (starting a run) is now 40 per minute per user, up from 20 per hour. The hourly budget was exhausted by ordinary use, such as launching a workflow once per device.
- Sessions last longer and are no longer signed out by sign-in traffic. The access token now lives 60 minutes (was 15) and the refresh token 30 days (was 7), set by `Jwt:AccessTokenMinutes` and `Jwt:RefreshTokenDays`. Token refresh has its own `auth_refresh` rate limit, 120 per minute per client IP. It used to share the 5-per-minute `auth_login` budget, so several tabs or users behind one address refreshing at once got 429 and were sent back to the login page. `auth_login` (login and bootstrap) is now 30 per minute per client IP; the per-account lockout (5 failed attempts) is unchanged.

### Fixed

- Rate-limit documentation now matches the enforced limits. The import guide said AI snippet generation had a budget of 30 per hour; the `ai_chat` limit is 60 per minute per user. The source comments in `RateLimitingConfiguration.cs` were corrected the same way.

## 0.9.0 - not yet published

The first tagged pre-release, in preparation. This entry gets its date and its `[0.9.0]` link when the `v0.9.0` tag and GitHub release are created. Until then, no version of FlowWeaver has been released.

### Upgrade notes (read before upgrading an existing instance)

- **Back up the Data Protection keyring first.** This version encrypts `Integration.AuthConfig` at rest with the keyring. On first start it rewrites existing plaintext rows encrypted, and from then on those integration credentials can only be read with that keyring. An instance that loses or replaces its keyring has to re-enter them. See [keyring rotation](./docs/ops/keyring-rotation.md).
- **Replace the development key if you used it.** A development keyring file was committed under `flow_weaver_backend/keyring/` before this version. It has been removed from the tree but remains in the repository history, so treat it as public. A deployment that used it should generate a new keyring and re-enter its stored credentials.
- The frontend image no longer contains `node_modules` (see Changed). Rebuild it with `run.sh`/`run.ps1` as usual; no configuration change is needed.

### Added

- Public repository documentation package: project overview, quick start, governance, maintainers, contribution, support and security policies; the Apache 2.0 scope notices for skills, specifications and workflows; and the legal set (licensing framework, DCO, trademark policy, third-party notices control).
- Sample workflows under `workflows/`: DNS audit (NetBox vs. Infoblox), NetBox devices CSV export, Windows KB inventory, silent packet loss check and leaf reachability check. Each ships as a full bundle export with a canvas capture and a README covering purpose, prerequisites, inputs, outputs, risk level and QA evidence.
- `examples/first-workflow/`: a reachability workflow bundle that imports on a new instance with no integration, credential or secret, with instructions to import, adapt, simulate and run it. A backend test imports every bundle under `examples/` into an empty instance, so an example that stops importing fails the build.
- CI (`.github/workflows/ci.yml`) with the `backend`, `frontend` and `repository` jobs, and a dependency audit (`.github/workflows/dependency-audit.yml`) that fails dependency changes with high or critical advisories and keeps a weekly tracking issue. See [CI and release gates](./docs/repository/CI_GATES.md).
- `tools/check-doc-links.mjs` checks every relative link in the Markdown files, including differences in letter case that Windows and macOS hide.
- [Releasing](./docs/repository/RELEASING.md): the release procedure, including every file that declares the version.

### Changed

- Updated the public entry points and quick start to describe the executable repository, AI-assisted draft creation, the visual editor, and current development checks. The README starts with direct links to installation, the first example, documentation, help and releases. The quick start now starts from `git clone`, separates the Docker and native requirements, says which features need an AI provider, and lists what to include when asking for help.
- Corrected the historical third-party inventory; included verbatim package licence and agreement files for `khroma` 2.1.0, `LibGit2Sharp.NativeBinaries` 2.0.323, QuestPDF 2025.1.5, and the listed json-everything versions; and separated completed classification from release-specific work.
- The backend declares its version (`0.9.0`), so bundle exports report it in `exported_by.version`. Earlier builds reported `1.0.0`, the .NET default.
- The frontend runtime image no longer copies `node_modules`. `adapter-node` bundles everything the server imports into `build/`, so those modules were only build tooling (Tailwind, the adapter and their dependencies). A local build of the image went from 652 MB to 266 MB.
- Node.js 22 is the documented and tested version for frontend development (`frontend/.nvmrc`), matching the `node:22-alpine` container.

### Removed

- A development ASP.NET Data Protection key file that had been committed under `flow_weaver_backend/keyring/`. The `keyring/` directory is now git-ignored. See the upgrade notes above.
- The GitLab branch-protection guide and the internal QA-round issue template, which did not apply to this repository.

### Fixed

- Documentation cleanup across the repository, the in-app manual and the agent skills: removed internal references, corrected statements that contradicted the code, fixed broken links and replaced example data with documentation-reserved values.
- `.gitignore` no longer matches source folders named `Secrets` on case-insensitive filesystems.
- The agent's `execute_operation` tool sent requests to integrations configured with `oauth2_client_credentials` without an `Authorization` header, so the upstream answered 401 (for example ServiceNow: "User is not authenticated") while the integration health check reported `healthy`. The REST executor now applies integration auth through `IIntegrationAuthApplier`, which fetches and caches the OAuth access token; a failed token grant is returned as an explicit auth error instead of an anonymous call.
- `integration_action` steps dropped every query parameter that was not a JSON string, without an error, so `"query": {"limit": 1000}` reached NetBox with no limit and returned its default page of 50 results. Numbers and booleans are now sent as their JSON text (`limit=1000`, `has_primary_ip=true`), and an array repeats the key once per element (`status=active&status=planned`). Objects and nulls are still left out.
- `integration_action` steps left a path placeholder unfilled when its value in `params` was a number, so `{"id": 42}` produced `/api/dcim/devices/{id}/` and a 404. Numeric and boolean values now fill the placeholder like their string form.
- The conformance kit integrity test failed on Windows checkouts made with `core.autocrlf=true`, the Git for Windows default. Git converted every vector to CRLF, which changed the byte digest without any change to the contract. `.gitattributes` now keeps `conformance/**` at LF on every platform, the test names line endings as the cause when it finds CR bytes, and [conformance/OWNERSHIP.md](./conformance/OWNERSHIP.md) documents how the digest is computed.
- `GitBranchPublishTests` built the test remote's URL as `"file:///" + path`, which gave Linux paths a fourth slash and failed four tests outside Windows.
- `npm run check:hints` failed on a clean checkout: the snippet form's "Start from" selector had no help entry, and the unused `chat.agent` entry was dead copy.

### Security

- `Integration.AuthConfig` is now encrypted at rest with the Data Protection keyring. See the upgrade notes above.
- Backend: MailKit 4.17.0 to 4.18.2, which brings `System.Security.Cryptography.Pkcs` 10.0.13 and fixes [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx) (high) and [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) (moderate), both denial of service. `dotnet list package --vulnerable` now reports no vulnerable packages.
- Frontend: updated `vite` (7.3.7), `svelte` (5.57.2), `@sveltejs/kit` (2.70.3), `mermaid` (11.17.2) and their transitive packages (`devalue`, `dompurify`, `postcss`, `nanoid`, `picomatch`, `source-map-js`, `uuid`, `esbuild`), and `zod` from 3 to 4.6.7. `npm audit` went from 17 entries (9 high, 5 moderate, 3 low) to 8 (2 moderate, 6 low, no high).

### Known limitations

- **Pre-1.0 pre-release.** v0.9.0 is the first tagged pre-release; setup and interfaces may change before 1.0. Verify a deployment in your own environment before using it for operational work.
- **Remaining npm advisories, evaluated and not fixed in this version.** `postcss-selector-parser` 6.0.10, through `@tailwindcss/typography` ([GHSA-rj75-hqrm-r3gf](https://github.com/advisories/GHSA-rj75-hqrm-r3gf), moderate), only parses the project's own stylesheets at build time and is not in the runtime image. `cookie` 0.6.0, through `@sveltejs/kit` 2.x ([GHSA-pxg6-pf52-xh8x](https://github.com/advisories/GHSA-pxg6-pf52-xh8x), low), affects serializing cookies with untrusted names, paths or domains, and the frontend sets no cookies; the fix needs SvelteKit 3. `katex` 0.16, through `mermaid` 11 ([GHSA-238p-pmpm-9mq7](https://github.com/advisories/GHSA-238p-pmpm-9mq7), low), needs an existing prototype pollution to be exploitable, and diagrams render with `securityLevel: 'strict'`; the fix needs Mermaid 12. The weekly dependency audit tracks all three.
- **No prebuilt images.** Installation builds the backend and frontend images from source with Docker.
- **The third-party notice is a historical inventory, not yet a complete release notice.** The [release work](./THIRD-PARTY-NOTICES.md#4-resolved-classifications-and-remaining-release-work) includes packaging third-party licence texts with the built artifacts, recording commercial-term decisions, and regenerating the inventory from those artifacts. The current public checkout has no committed NuGet lockfiles or `deploy/requirements.txt`, and its container bases use tags rather than digest pins.
- Supported versions are listed in [SECURITY.md](./SECURITY.md#supported-versions).

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
