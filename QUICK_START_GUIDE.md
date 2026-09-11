# Flow Weaver — Quick start

From a clean checkout to a workflow that ran, on one page. Everything deeper
links out; nothing here is repeated from `deploy/README.md`.

Flow Weaver is a network-automation platform: a workflow is a directed graph of
**snippets** (Python, Ansible, REST, transforms) executed against **devices** or
**device pools**, with runs tracked end to end and an AI assistant that can author
and repair them.

---

## 1. What you need

| Path | Requirements |
|---|---|
| **Docker** (recommended) | Docker Engine + Compose v2. Nothing else. |
| **Native** | .NET 10 SDK, Node 20+, PostgreSQL 17 reachable and writable. `python_snippet` steps additionally need Linux with `bwrap`. |

You also need a JWT signing key. Generate one with `openssl rand -base64 48` —
the backend **refuses to boot** outside Development with a short or known-default
key.

> Every command has a PowerShell twin: `setup.sh`/`setup.ps1`, `run.sh`/`run.ps1`.
> Where a plain shell command is shown (`cp`, `openssl`, `curl`) it assumes bash —
> use Git Bash or WSL on Windows, or the obvious equivalent.

---

## 2. The fast path: the first-run wizard

```bash
cd deploy
chmod +x setup.sh          # first time only
./setup.sh
```

```powershell
cd deploy
.\setup.ps1
```

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

---

## 3. Without the wizard

```bash
cd deploy
cp .env.example .env
#   -> set JWT_KEY (required) and POSTGRES_PASSWORD
./run.sh -y                 # .\run.ps1 -Yes on Windows
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

Sign in at <http://localhost:3000>, then follow the in-app manual — it is written
against the running UI and stays current with it:

- **`/docs/getting-started`** — the app shell, the role model, and the fastest
  path from login to a run.
- `/docs` — the full manual, one chapter per screen.

The short version, once you are signed in:

1. **Credential** → `/credentials`. How devices are reached.
2. **Device** → `/devices`. Name, IP, platform, and that credential.
3. **Workflow** → `/workflows` → *New workflow*. The DAG editor opens with
   `__start__` and `__end__`; drag a snippet in between and connect them.
   Save with <kbd>Ctrl</kbd>/<kbd>⌘</kbd>+<kbd>S</kbd>.
4. **Simulate** → walks the graph without touching a device and reports ordering
   problems and unresolved templates.
5. **Run** → collects the runtime input (from the workflow's input schema) and
   the target devices, scoped to the workflow's environment.
6. **Watch** → `/runs/{id}/monitor`. Live node status, per-step logs, input and
   output. A failed node offers *Fix with AI*.

New workflows start in `draft` and can never fan out to production hardware:
promotion is `draft → qa → production`, and each device carries its own
`allow_draft` / `allow_qa` / `allow_production` flags.

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
dotnet test                              # backend unit tests
./coverage.sh                            # tests + HTML report + headline %
./coverage.sh --no-html                  # faster, text summary only

cd frontend
npm run check                            # svelte-check (types + a11y)
npm run check:hints                      # field-hint coverage
npx playwright test                      # e2e, needs the stack running
```

`conformance/` holds the cross-implementation `workflow.v1` suite — the same
bundle format Nashira reads and writes. `qa/` holds the authz and sweep scripts.

---

## 7. When it does not come up

| Symptom | Cause |
|---|---|
| Backend exits at boot | `JWT_KEY` missing, too short, or a known default. |
| `bwrap: setting up uid map: Permission denied` | The Linux host restricts unprivileged user namespaces. Run `sudo ./deploy/setup-host.sh` once, then `./run.sh restart`. See `docs/ops/python-sandbox-host-requirements.md`. |
| POSTs rejected by SvelteKit | `FRONTEND_ORIGIN` does not match the URL the browser actually uses. Fix it in `deploy/.env`. |
| Every request shows one IP in the audit trail | `TRUSTED_PROXIES` does not match `COMPOSE_SUBNET`. The scripts write them together — do not edit only one. |
| `RemoveMultiTenancy: <table> has N duplicate group(s)` | The database came from a multi-tenant install. See `deploy/ops/consolidate-to-single-tenant.sql`. |

Everyday diagnostics — `run.sh`/`run.ps1` wrap these, or use compose directly
from `deploy/`:

```bash
./run.sh logs backend | grep -i "migrations applied"   # confirm a healthy start
./run.sh logs backend                                  # follow the backend
./run.sh down                                          # stop

docker compose ps                                      # what is up, and healthy
docker compose exec db psql -U flowweaver -d flowweaver
```

---

## 8. Where to go next

| | |
|---|---|
| [`deploy/README.md`](deploy/README.md) | Every deployment option: external databases, ports, subnets, tunnels, host setup, backup and DR. |
| [`docs/workflow-bundles.md`](docs/workflow-bundles.md) | The portable bundle format — how a workflow moves between instances (and to Nashira). |
| [`docs/subflows.md`](docs/subflows.md) | Reusable sub-graphs. |
| [`docs/imports.md`](docs/imports.md) | Importing workflows and inventory. |
| [`docs/permissions.md`](docs/permissions.md) | Roles, and the per-user tool permissions the assistant obeys. |
| [`docs/mcp.md`](docs/mcp.md) · [`docs/vendors.md`](docs/vendors.md) | MCP servers, and the vendor-command catalogue. |
| [`docs/ops/`](docs/ops/) | Branch protection, keyring rotation, load testing, disaster recovery. |
| `/docs` in the running app | The user manual, one chapter per screen. |
