# Running flow-weaver

## First install: `setup.sh` / `setup.ps1`

For a brand-new instance, the first-run wizard does everything in one
interactive command:

```bash
./setup.sh          # Linux / macOS  (first time: chmod +x setup.sh)
```
```powershell
.\setup.ps1         # Windows
```

Steps it covers — each one detects existing state and can be skipped:

1. **Deploy** — with Docker (delegates to `run.sh`/`run.ps1`, which
   configure `deploy/.env` interactively), in **native** mode without
   Docker (`dotnet` + `node` against an existing Postgres;
   `python_snippet` steps need Linux with bwrap), or `skip` if the
   instance is already running. On Linux, `run.sh` verifies the
   python sandbox after starting and points to `setup-host.sh` if the
   host restricts user namespaces (see below).
2. **Initial credentials** — creates the admin account via
   `POST /api/auth/bootstrap` (available in any environment ONLY while no
   user exists yet). You pick the username and password, or a strong
   password is generated and shown exactly once.
3. **AI provider** — registers OpenAI / Anthropic / Gemini / Ollama and
   runs a live connectivity test.
4. **Agent** — creates the default "assistant" agent (all tools) so
   `/ai/chat` works immediately.

Reconfigure an already-deployed instance without touching the deploy:
`./setup.sh configure`. Stop a native deployment: `./setup.sh stop`.

## Day-to-day operation: `run.sh` / `run.ps1`

Two equivalent scripts, one per platform. They do the same thing and take
the same options; only the flag syntax differs.

| | Linux / macOS | Windows |
|---|---|---|
| Interactive | `./run.sh` | `.\run.ps1` |
| No questions | `./run.sh -y` | `.\run.ps1 -Yes` |
| Follow logs | `./run.sh logs backend` | `.\run.ps1 logs backend` |
| Stop | `./run.sh down` | `.\run.ps1 down` |

On Linux, the first time: `chmod +x run.sh`.

Whatever you answer (or pass as flags) is saved to `deploy/.env`, so the
next run reuses the configuration. To see what it would do without
executing anything: `--dry-run` / `-DryRun`.

## Database: two modes

**`docker`** (default) — the compose project starts its own Postgres with a
persistent volume. This is the mode for development.

```bash
./run.sh -y                               # all defaults
./run.sh -y --postgres-port 5433          # publish Postgres on another port
```

**`external`** — you point at a Postgres you already have. The `db` service
doesn't start and its dependency is dropped, so only backend, worker and
frontend come up.

```bash
./run.sh -y --db external \
  --db-host 10.0.0.5 --db-port 5432 \
  --db-name flowweaver --db-user fw --db-password 's3cr3t'
```

```powershell
.\run.ps1 -Yes -DbMode external `
  -DbHost 10.0.0.5 -DbName flowweaver -DbUser fw -DbPassword 's3cr3t'
```

If the database lives **on the same machine** as Docker, use
`host.docker.internal`: inside a container, `localhost` is the container
itself. If you type `localhost` the script detects it and substitutes the
right name, warning you. On Linux that works because the
`compose.external-db.yml` overlay maps that name to the host gateway.

The database must exist and the user must be allowed to create tables: the
backend applies the migrations itself at startup.

## Ports and configuration

```bash
./run.sh -y --backend-port 8090 --frontend-port 3001 --env Production
```

The script warns when a port is already taken on the host.
`FRONTEND_ORIGIN` is derived from the frontend port; if you serve behind a
real domain, answer it at the prompt or edit it in `.env` — SvelteKit
rejects POSTs when the origin doesn't match the URL the browser uses.

## Networking

`COMPOSE_SUBNET` pins the project's subnet, and `TRUSTED_PROXIES` (the
range the backend accepts `X-Forwarded-For` headers from) **must match**.
The scripts always write them together, and if the chosen subnet is taken
by another Docker network they look for a free `/16` between 172.16 and
172.31.

The `10.x` and `192.168.x` ranges are avoided on purpose: on a host that
manages network equipment those are usually the management networks, and an
overlapping Docker subnet would blackhole traffic to the very devices this
platform drives.

To force one: `--subnet 172.20.0.0/16`.

## Other options

| Flag (bash / PowerShell) | What for |
|---|---|
| `--services "backend frontend"` / `-Services backend,frontend` | Start only some services |
| `--tunnel` / `-Tunnel` | Also start `cloudflared` (public ingress for webhooks) |
| `--build` / `-Build` | Force a rebuild (by default it only builds when the image is missing) |
| `--pull` / `-Pull` | Pull base images before building |
| `--no-dev-overrides` / `-NoDevOverrides` | Ignore `docker-compose.override.yml` |

`docker-compose.override.yml` attaches backend and worker to the external
`shared-net` network (NetBox, netora-agent, the dev environment's SR Linux
nodes). It is included automatically **only when that network exists** on
the host, so it stays out of the way on a clean machine.

## Python sandbox on Linux hosts: `setup-host.sh`

`python_snippet` steps run inside a bubblewrap sandbox, which needs
unprivileged user namespaces. Several distros restrict those **on the host**
(Ubuntu 23.10+/24.04, RHEL, older Debian — each with a different knob), and
no container flag can override host policy. On such a host every python step
fails with `bwrap: setting up uid map: Permission denied` while everything
else works.

`run.sh` verifies the sandbox automatically after every `up` (it runs bwrap
inside the worker, as the worker's user) and tells you if the host needs
fixing. The fix is one command, run once per host:

```bash
sudo ./deploy/setup-host.sh   # detects the distro's restriction, applies the
                              # narrowest fix (AppArmor profile for bwrap
                              # alone when possible), verifies it
./run.sh restart
```

If the host cannot be changed, or the deployment simply doesn't use python
snippets, there are alternatives (including turning the feature off) — the
options and their security trade-offs are in
`docs/ops/python-sandbox-host-requirements.md`.

## Verifying it came up

```bash
./run.sh logs backend | grep -i "migrations applied"
```

The backend applies the EF migrations at startup with retries. If you see
`RemoveMultiTenancy: <table> has N duplicate group(s)` instead, the
database comes from a multi-tenant install and must be consolidated first —
see `ops/consolidate-to-single-tenant.sql` and `../company_remove.md`.

## Operations scripts

| Script | When |
|---|---|
| `ops/precheck-single-tenant.sql` | Before migrating: checks for duplicates that would block `RemoveMultiTenancy` |
| `ops/consolidate-to-single-tenant.sql` | If the precheck fails: collapses to a single company (destructive) |
| `ops/migrate-pyenv-to-site.sh` | Moves pip packages from `/app/pyenv/<companyId>` to `/app/pyenv/site` |
| `ops/pg_basebackup.sh`, `ops/dr-drill.sh` | Backup and disaster-recovery drill |
