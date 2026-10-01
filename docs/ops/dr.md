# Disaster recovery — Postgres

FlowWeaver stores the entire source of truth (workflows, runs, jobs queue,
audit, traces, integrations, credentials) in a single Postgres instance.
A loss of the database is a total loss of the platform. This document
describes how to back it up, how to restore it, and how to prove the
restore works.

## Targets

Recommended targets for a production deployment:

| Metric | Target |
|---|---|
| Recovery Point Objective (RPO) | ≤ 5 minutes |
| Recovery Time Objective (RTO) | ≤ 60 minutes |
| Backup retention (full) | 30 days |
| Backup retention (WAL) | 7 days |
| Restore drill cadence | quarterly |

RPO ≤ 5 min requires continuous WAL archiving. The repository ships a
full-backup script only (`deploy/ops/pg_basebackup.sh`); run nightly, it gives
RPO = 24 h. To reach the 5-minute target, add WAL archiving with a tool such as
pgbackrest (configured by the operator; see below).

## What ships in the repository

| Item | What it does |
|---|---|
| `db` service in `deploy/docker-compose.yml` | `postgres:17-alpine`, data in the `flow-weaver_db_data` volume. No WAL archiving and no backup sidecar are configured. |
| [`deploy/ops/pg_basebackup.sh`](../../deploy/ops/pg_basebackup.sh) | Full `pg_basebackup` (tar, gzip, WAL streamed) into `BACKUP_DIR/base-<UTC stamp>/`, pruning directories older than `BACKUP_RETENTION_DAYS` (default 30). Needs `PGHOST`, `PGUSER`, `BACKUP_DIR` (and `PGPASSWORD` / `PGPORT` as required). |
| [`deploy/ops/dr-drill.sh`](../../deploy/ops/dr-drill.sh) | Restore drill against a **pgbackrest** repository (see "Quarterly drill checklist"). |

## Full backups with `pg_basebackup.sh`

Run the script from a host or container that has the PostgreSQL 17 client
tools and can reach the database:

```bash
PGHOST=db.example.internal PGPORT=5432 PGUSER=flowweaver PGPASSWORD='<password>' \
BACKUP_DIR=/srv/flow-weaver/backups BACKUP_RETENTION_DAYS=30 \
  ./deploy/ops/pg_basebackup.sh
```

The role needs the `REPLICATION` attribute (the compose `POSTGRES_USER` is a
superuser) and `pg_hba.conf` must allow a replication connection from where
the script runs. A sample nightly cron line is in the script header.

Copy each `base-<stamp>/` directory off the host (for example to encrypted
object storage) together with the keyring snapshot (see "Backup of the keyring").

## Continuous WAL archiving (optional, operator-configured)

For the 5-minute RPO, configure WAL archiving on the database. pgbackrest is a
good fit because it manages WAL retention, encryption and parallel restore. It
is not included in the `postgres:17-alpine` image or in the compose file, so
install and run it yourself (on the database host, in a custom image, or in a
sidecar with access to the data directory). Example settings:

```ini
# postgresql.conf
wal_level = replica
archive_mode = on
archive_command = 'pgbackrest --stanza=main archive-push %p'
archive_timeout = 60        # force a WAL switch every 60 s, well inside the 5-minute RPO
max_wal_senders = 3
```

```ini
# pgbackrest.conf
[main]
pg1-path=/var/lib/postgresql/data
repo1-type=s3
repo1-s3-bucket=flow-weaver-backups
repo1-s3-region=us-east-1
repo1-retention-full=30
repo1-retention-archive=7
repo1-cipher-type=aes-256-cbc
repo1-cipher-pass=<sourced from secret store>
process-max=4
```

Suggested schedule with pgbackrest:

| Job | Cadence |
|---|---|
| `pgbackrest stanza-create` | once, on stanza setup |
| `pgbackrest backup --type=full` | weekly, Sundays 02:00 UTC |
| `pgbackrest backup --type=diff` | daily, 02:00 UTC (Mon–Sat) |
| WAL archive push | continuous (`archive_command`, at least every 60 s) |
| `pgbackrest verify` | weekly, after the full backup |
| Restore drill on staging | quarterly |

## Restore procedure

Pre-conditions: the backup to restore is reachable from the host, and the
target data volume can be emptied.

1. Stop the FlowWeaver backend and worker:
   ```bash
   docker compose -f deploy/docker-compose.yml stop backend worker
   ```
2. Stop the database and recreate its volume empty:
   ```bash
   docker compose -f deploy/docker-compose.yml stop db
   docker compose -f deploy/docker-compose.yml rm -f db
   docker volume rm flow-weaver_db_data
   docker volume create flow-weaver_db_data
   ```
3. Restore the data directory into the empty volume.
   - **From a `pg_basebackup.sh` backup** (no point-in-time recovery; you get
     the state at the end of that backup):
     ```bash
     BACKUP=/srv/flow-weaver/backups/base-<stamp>
     docker run --rm -v flow-weaver_db_data:/data -v "$BACKUP":/backup:ro \
       postgres:17-alpine sh -c '
         tar -xzf /backup/base.tar.gz -C /data &&
         mkdir -p /data/pg_wal && tar -xzf /backup/pg_wal.tar.gz -C /data/pg_wal &&
         chown -R postgres:postgres /data && chmod 700 /data'
     ```
   - **From pgbackrest**, run the restore wherever pgbackrest is installed with
     the volume mounted as `pg1-path`. Latest available WAL
     (RPO ≈ `archive_timeout`):
     ```bash
     pgbackrest --stanza=main restore --delta
     ```
     Point in time (incident response):
     ```bash
     pgbackrest --stanza=main --type=time \
       --target="2026-05-08 14:32:00 UTC" restore --delta
     ```
4. Start Postgres and wait until it accepts connections (with pgbackrest,
   wait for `archive recovery complete` in the log):
   ```bash
   docker compose -f deploy/docker-compose.yml up -d db
   ```
5. Run smoke checks before exposing traffic (tables are snake_case, columns
   PascalCase):
   ```bash
   docker compose -f deploy/docker-compose.yml exec db psql -U flowweaver -d flowweaver -c \
     "select count(*) from workflows;
      select count(*) from workflow_runs where \"Status\" = 'running';"
   ```
6. Restart the application:
   ```bash
   docker compose -f deploy/docker-compose.yml start backend worker
   ```
7. Monitor `/admin/audit` and `/admin/traces` for the first 30 minutes.
   In-flight runs will reclaim via `JobReclaimHostedService`. Cancel any
   stale `running` rows that pre-date the restore.

## Quarterly drill checklist

The drill is automated by [`deploy/ops/dr-drill.sh`](../../deploy/ops/dr-drill.sh)
for deployments that use pgbackrest (it needs `PGBACKREST_STANZA` and
`PGBACKREST_REPO`; it does not read `pg_basebackup.sh` output). The script:

1. Spins up a sidecar Postgres on a non-conflicting port.
2. Installs pgbackrest in the sidecar and restores the latest backup into
   the sidecar's volume.
3. Runs sanity queries (workflow / integration / credential counts; last
   run timestamp).
4. Asserts the elapsed time stayed under the RTO budget (default 60 min,
   `DR_DRILL_RTO_BUDGET` in seconds).
5. Tears the sidecar down — never touches the production volume.

Schedule it as cron (sample in the script header). Reports land under
`/var/log/flow-weaver/dr-drills/` (or `DR_DRILL_REPORT_DIR`) with a UTC
timestamp.

For `pg_basebackup.sh` backups, run the drill by hand: follow the restore
procedure above against a staging stack and time it.

Manual checklist for the operator-driven walkthrough:

- Pick a date inside the quarter, schedule with operations.
- Run the script against the staging credential set, not production.
- Review the produced report. The non-zero exit code indicates either a
  failed restore or RTO budget overrun.
- File the report under the operations log: timing, deviations, action
  items.
- Update this document if anything changes.

## Automated recovery invariants

The drill above proves the *data* survives a restore. The application-level
"restart / reclaim / cancellation do **not** duplicate effects" invariants are
pinned by `ExecutionRobustnessTests` in the backend test suite:
reclaimed runs resume their existing `step_runs` instead of re-creating them,
duplicate per-device step_runs collapse to one, and the promotion compensation
gate forces side-effecting steps to carry a rollback path. Recovery of in-flight
work itself is mediated entirely by the job lease: a crashed worker's `claimed`
job expires after 5 min and `JobReclaimHostedService` (30 s sweep) returns it to
`pending` for another worker.

Current limitations: reclaim has no max-attempts cap;
a reclaimed *step* job re-runs its handler with no idempotency claim (at-least-once
— `non_reversible` steps re-apply, so lean on the compensation gate); and
`Complete/Fail` guard on job `Status` but not `ClaimedBy`.

## Failure modes and responses

| Failure | Detection | Response |
|---|---|---|
| WAL archive push failing | Postgres logs `archive command failed` | Act promptly: failed archiving keeps WAL on disk, which eventually fills the volume and halts writes. |
| Object storage unreachable | Backup job exits non-zero in cron | Investigate IAM / network. Treat backups missing for more than 24 h as urgent. |
| `pg_basebackup.sh` fails | Non-zero exit, error in the cron log | Check connectivity, the role's `REPLICATION` attribute and `pg_hba.conf`. |
| Restore drill exceeds RTO | Quarterly drill report | Find the root cause before the next drill. |
| pgbackrest cipher pass lost | Restore fails with decrypt error | Backups are unrecoverable. Pass must be in the secret store with at least two break-glass accesses. |

## Backup of the keyring

Postgres holds the encrypted credentials, but the **DataProtection keyring**
(in `backend_keyring` Docker volume) holds the keys to decrypt them. Both
must be backed up together. Snapshot the volume in the same window as the
Postgres full backup. See [`docs/ops/keyring-rotation.md`](./keyring-rotation.md).

## See also

- [`docs/ops/keyring-rotation.md`](./keyring-rotation.md) — DataProtection keyring lifecycle.
