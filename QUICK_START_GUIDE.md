# FlowWeaver — Quick start

From a clean checkout to a workflow that ran, on one page. Everything deeper
links out; nothing here is repeated from `deploy/README.md`.

FlowWeaver is a network-automation platform: a workflow is a directed graph of
**snippets** (Python, Ansible, REST, transforms) executed against **devices** or
**device pools**, with runs tracked end to end and an AI assistant that can author
and repair them.

---

## 1. What you need

Pick one mode and follow only its column of requirements.

| Mode | Who it is for | Requirements on the host |
|---|---|---|
| **Docker** (recommended) | Trying FlowWeaver or running an instance | Git (or a source archive), Docker Engine with Compose v2, and either Bash and curl (Linux/macOS wizard) or PowerShell (Windows). The containers build and run the backend and frontend, so .NET and Node are **not** needed on the host. |
| **Native** | Developing FlowWeaver itself | .NET 10 SDK, Node.js 22 (the version CI and the frontend container use), and PostgreSQL 17 reachable and writable. `python_snippet` steps additionally need Linux with `bwrap`. |

**AI provider (optional).** Only the AI assistant (`/ai/chat`) and **Fix with AI**
need a provider: an OpenAI, Anthropic or Gemini API key, or an Ollama endpoint.
Inventory, the visual editor, import, simulation, runs and promotion work without
one, and the wizard lets you skip the provider step and add it later.

The backend needs a JWT signing key. The wizard and `run.sh`/`run.ps1` generate one
for you. If you configure `deploy/.env` by hand, generate it with
`openssl rand -base64 48`: the backend **refuses to boot** outside Development with
a short or known-default key.

> Every command has a PowerShell twin: `setup.sh`/`setup.ps1`, `run.sh`/`run.ps1`.
> Where a plain shell command is shown (`cp`, `openssl`, `curl`) it assumes bash —
> use Git Bash or WSL on Windows, or the obvious equivalent.

---

## 2. The fast path: the first-run wizard

Get the code, then start the wizard from `deploy/`:

```bash
git clone https://github.com/Isaza-Labs/flow-weaver.git
cd flow-weaver/deploy
chmod +x setup.sh run.sh   # first time only
./setup.sh
```

```powershell
git clone https://github.com/Isaza-Labs/flow-weaver.git
cd flow-weaver\deploy
.\setup.ps1
```

This clones the development branch, `main`. To install a published release
instead, see [Releases](https://github.com/Isaza-Labs/flow-weaver/releases): each
release lists the `git clone --branch <tag>` command for its version.

One interactive command takes you all the way. Each step detects existing state
and can be skipped, so it is safe to re-run:

1. **Deploy** — Docker (delegates to `run.sh`/`run.ps1`, which writes
   `deploy/.env` for you), native (`dotnet` + `node` against an existing
   Postgres), or `skip` if something is already running.
2. **Initial credentials** — creates the admin account via
   `POST /api/auth/bootstrap`. You choose the username and password, or a strong
   one is generated and shown **exactly once**.
3. **AI provider** — registers OpenAI / Anthropic / Gemini / Ollama and runs a
   live connectivity test.
4. **Agent** — creates the default `assistant` agent so `/ai/chat` works
   immediately.

Reconfigure an instance that is already deployed: `./setup.sh configure`.
Stop a native deployment: `./setup.sh stop`.

When it finishes:

| | URL |
|---|---|
| App | <http://localhost:3000> |
| API | <http://localhost:8080> |
| API reference | <http://localhost:8080/scalar> |

Installation ends here: the wizard has printed these URLs and you can sign in at
the app with the admin account from step 2. The first exercise starts in
[section 4](#4-your-first-workflow).

---

## 3. Without the wizard

From the root of the clone:

```bash
cd deploy
cp .env.example .env
#   -> set JWT_KEY (required) and POSTGRES_PASSWORD
bash ./run.sh -y            # .\run.ps1 -Yes on Windows
```

Then create the first admin — this endpoint answers on a fresh install in any
environment, and 404s permanently as soon as any user exists:

```bash
curl -X POST http://localhost:8080/api/auth/bootstrap \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"<a strong password>"}'
```

Omit the body and you get `admin` plus a generated one-time password in the
response. Save it — it is not shown again.

Everything you answer or pass as a flag is persisted to `deploy/.env`, so the
next `run.sh` reuses it. Ports, external databases, subnets and the
`cloudflared` tunnel are all covered in **[`deploy/README.md`](deploy/README.md)**.

---

## 4. Your first workflow

Sign in at <http://localhost:3000>. The in-app [getting-started manual](./frontend/src/routes/docs/getting-started/+page.svelte) is available at `/docs/getting-started`; `/docs` lists every chapter.

For a safe first run, register a lab device you are authorized to manage at
`/devices`. Give it a name and IP address and enable its **draft** environment
flag. The built-in `ping` snippet needs no credential. For an SSH workflow,
configure a credential and platform as described in the in-app manual.

### Describe it to the assistant

Open `/ai/chat` and ask for a **draft** workflow. For example:

> Create a draft workflow named lab-reachability-check that uses the built-in
> ping snippet for my registered lab device. Ask me which device to use if the
> target is unclear. Do not run or promote it.

The assistant may ask for a target or other missing details. Review the
resulting workflow in `/workflows`: confirm the device, node, connections,
environment and inputs. If the assistant cannot create it, check that the AI
provider and default agent are configured and that your account can create
workflows. You can also use the visual editor or the published example below.

### Or import the published example

[`examples/first-workflow/`](examples/first-workflow/README.md) holds a small
reachability workflow exported from FlowWeaver. Import it at **Workflows → Import**;
it lands as a draft and needs no integration, credential or secret. Its README
explains what to adapt, how to simulate and run it, and what to expect.

### Build it in the visual editor

At `/workflows`, choose **New workflow**, give it a name and open it from the
Draft list. Drag the built-in `ping` snippet between `__start__` and
`__end__`, connect the nodes, then save. Search for `ping` if it is under
the collapsed **Unproven** section.

### Validate and run

1. Select **Simulate** in the editor. It checks graph structure and configuration
   without touching a device; it is not a live network test.
2. Review any reported issues and the chosen draft target.
3. Select **Run** only when ready. The run dialog scopes available devices to the
   workflow's environment.
4. Inspect status, logs, input and output in the run monitor. A failed step
   offers **Fix with AI**.

Draft, QA and production targets are controlled by each device's environment
flags. Promotion to production has additional QA evidence and approval gates;
see the [QA lab chapter](./frontend/src/routes/docs/qa-lab/+page.svelte).

---

## 5. Local development (no Docker)

Two terminals. Postgres must be running and reachable with the credentials in
`flow_weaver_backend/appsettings.json`
(`Host=localhost;Database=flowweaver;Username=flowweaver;Password=flowweaver`).

```bash
# backend
dotnet run --project flow_weaver_backend --urls http://localhost:8080
```

```bash
# frontend
cd frontend
npm ci
npm run dev        # http://localhost:5173
```

> **Use `--urls http://localhost:8080`.** The Vite dev server proxies `/api`,
> `/openapi` and `/scalar` to a hard-coded `http://localhost:8080`, while
> `launchSettings.json` would otherwise start the backend on `5228` — where the
> proxy will not find it.

In `Development` a fresh database is seeded with `admin` / `admin` so the login
page works straight away, the bootstrap endpoint is always available, EF
migrations are applied on boot with retry, and the seeders populate snippets,
vendor commands and the built-in API specs. Production deployments go through
`/api/auth/bootstrap` + `/api/users` instead.

---

## 6. Tests

```bash
dotnet test flow_weaver_backend.Tests/flow_weaver_backend.Tests.csproj

cd frontend
npm run check                            # svelte-check (types + a11y)
npm run check:hints                      # field-hint coverage
npm run check:labels                     # labels audit
npx playwright test                      # e2e, needs the stack running
```

`conformance/` holds the cross-implementation `workflow.v1` suite.
The Playwright config and end-to-end tests live under `frontend/`.
`node tools/check-doc-links.mjs` checks the relative links in the Markdown files.
CI runs the same checks on every pull request; see
[CI and release gates](docs/repository/CI_GATES.md).

---

## 7. When it does not come up

| Symptom | Cause |
|---|---|
| Backend exits at boot | `JWT_KEY` missing, too short, or a known default. |
| `bwrap: setting up uid map: Permission denied` | The Linux host restricts unprivileged user namespaces. From `deploy/`, run `sudo bash ./setup-host.sh` once, then `bash ./run.sh restart`. See [host requirements](./docs/ops/python-sandbox-host-requirements.md). |
| POSTs rejected by SvelteKit | `FRONTEND_ORIGIN` does not match the URL the browser actually uses. Fix it in `deploy/.env`. |
| Every request shows one IP in the audit trail | `TRUSTED_PROXIES` does not match `COMPOSE_SUBNET`. The scripts write them together — do not edit only one. |

Run these everyday diagnostics from `deploy/`. The `run.sh`/`run.ps1`
scripts wrap Compose, or you can use Compose directly:

```bash
bash ./run.sh logs backend | grep -i "migrations applied"   # confirm a healthy start
bash ./run.sh logs backend                                  # follow the backend
bash ./run.sh down                                          # stop

docker compose ps                                      # what is up, and healthy
docker compose exec db psql -U flowweaver -d flowweaver
```

### Asking for help

Ask in [Questions and help](https://github.com/Isaza-Labs/flow-weaver/discussions/categories/q-a)
(see [SUPPORT.md](SUPPORT.md) for every route). Include:

- **Version or commit:** the release tag you installed, or the output of
  `git rev-parse --short HEAD` in your clone.
- **Installation mode:** wizard with Docker, `run.sh`/`run.ps1` without the wizard,
  or native.
- **Symptom:** what you did, what you expected and what happened.
- **Relevant log lines:** for example from `bash ./run.sh logs backend`.

Remove credentials, tokens, `.env` contents, internal hostnames and addresses, and
customer data before posting.

---

## 8. Where to go next

| | |
|---|---|
| [`deploy/README.md`](deploy/README.md) | Every deployment option: external databases, ports, subnets, tunnels, host setup, backup and DR. |
| [`docs/workflow-bundles.md`](docs/workflow-bundles.md) | The portable bundle format — how a workflow moves between instances. |
| [`docs/subflows.md`](docs/subflows.md) | Reusable sub-graphs. |
| [`docs/imports.md`](docs/imports.md) | Importing workflows and inventory. |
| [`docs/permissions.md`](docs/permissions.md) | Roles, and the per-user tool permissions the assistant obeys. |
| [`docs/mcp.md`](docs/mcp.md) · [`docs/vendors.md`](docs/vendors.md) | MCP servers, and the vendor-command catalogue. |
| [`docs/ops/`](docs/ops/) | Keyring rotation, load testing, disaster recovery, sandbox host requirements. |
| `/docs` in the running app | The user manual, one chapter per screen. |
