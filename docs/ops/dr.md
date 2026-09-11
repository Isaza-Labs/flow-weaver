# Disaster recovery — Postgres

FlowWeaver stores the entire source of truth (workflows, runs, jobs queue,
audit, traces, integrations, credentials) in a single Postgres instance.
A loss of the database is a total loss of the platform. This document
defines how we back it up, how we restore it, and how often we prove it.

## Targets

| Metric | Target |
|---|---|
| Recovery Point Objective (RPO) | ≤ 5 minutes |
| Recovery Time Objective (RTO) | ≤ 60 minutes |
| Backup retention (full) | 30 days |
| Backup retention (WAL) | 7 days |
| Restore drill cadence | quarterly |

RPO ≤ 5 min requires continuous WAL archiving. A nightly `pg_basebackup`
alone gives RPO = 24 h, which is not acceptable for production.

## Topology

```
  ┌──────────────────────┐    ┌─────────────────────────────┐
  │ Postgres 17 primary  │    │ Object storage (S3 / Blob)  │
  │  - WAL level=replica │───▶│  flow-weaver-backups/       │
  │  - archive_mode=on   │    │   ├── base/YYYYMMDD/        │
  │  - archive_command   │    │   └── wal/                  │
  └──────────┬───────────┘    └─────────────────────────────┘
             │
             ▼
  ┌──────────────────────┐
  │ pgbackrest container │
  │ (sidecar / cron job) │
  └──────────────────────┘
```

`pgbackrest` is preferred over a hand-rolled `pg_basebackup` + `cron`
because it manages WAL retention, encryption, and parallel restore.
The plain `pg_basebackup` script in `deploy/ops/pg_basebackup.sh` is the
fallback for environments that cannot run pgbackrest.

## Postgres configuration

```ini
# postgresql.conf
wal_level = replica
archive_mode = on
archive_command = 'pgbackrest --stanza=main archive-push %p'
archive_timeout = 60        # force a WAL switch every 60s for RPO ≤ 1 min
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

## Schedule

| Job | Cadence | Owner |
|---|---|---|
| `pgbackrest stanza-create` | once, on stanza setup | Operations |
| `pgbackrest backup --type=full` | weekly, Sundays 02:00 UTC | cron / k8s CronJob |
| `pgbackrest backup --type=diff` | daily, 02:00 UTC (Mon–Sat) | cron / k8s CronJob |
| WAL archive push | continuous (every 60 s) | Postgres `archive_command` |
| `pgbackrest verify` | weekly, after full backup | cron |
| Restore drill on staging | quarterly | Operations |

## Restore procedure

Pre-conditions: pgbackrest configured, target host reachable, S3 credentials
present, target Postgres data dir empty.

1. Stop the FlowWeaver backend and worker:
   ```bash
   docker compose -f deploy/docker-compose.yml stop backend worker
   ```
2. Stop and wipe the target Postgres:
   ```bash
   docker compose -f deploy/docker-compose.yml stop db
   docker volume rm flow-weaver_db_data
   ```
3. Bring up an empty Postgres container with the same version:
   ```bash
   docker compose -f deploy/docker-compose.yml up -d db
   ```
4. Restore the most recent backup. To restore to the latest available
   WAL (RPO ≈ archive_timeout):
   ```bash
   docker compose exec db pgbackrest --stanza=main restore --delta
   ```
   To restore to a specific moment in time (incident response):
   ```bash
   docker compose exec db pgbackrest --stanza=main \
     --type=time --target="2026-05-08 14:32:00 UTC" restore --delta
   ```
5. Start Postgres in recovery mode and wait for `archive recovery complete`.
6. Run smoke checks before exposing traffic:
   ```bash
   docker compose exec db psql -U flowweaver -d flowweaver -c \
     "select count(*) from \"Workflow\";
      select count(*) from \"WorkflowRun\" where status='running';"
   ```
7. Restart the application:
   ```bash
   docker compose -f deploy/docker-compose.yml start backend worker
   ```
8. Monitor `/admin/audit` and `/admin/traces` for the first 30 minutes.
   In-flight runs will reclaim via `JobReclaimHostedService`. Cancel any
   stale `running` rows that pre-date the restore.

## Quarterly drill checklist

The drill is automated by [`deploy/ops/dr-drill.sh`](../../deploy/ops/dr-drill.sh).
The script:

1. Spins up a sidecar Postgres on a non-conflicting port.
2. Restores the latest pgbackrest backup into the sidecar's volume.
3. Runs sanity queries (workflow / integration / credential counts; last
   run timestamp).
4. Asserts the elapsed time stayed under the RTO budget (default 60 min).
5. Tears the sidecar down — never touches the production volume.

Schedule it as cron (sample in the script header). Reports land under
`/var/log/flow-weaver/dr-drills/` with a UTC timestamp.

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
pinned by `ExecutionRobustnessTests` in the backend suite (run every CI build):
reclaimed runs resume their existing `step_runs` instead of re-creating them,
duplicate per-device step_runs collapse to one, and the promotion compensation
gate forces side-effecting steps to carry a rollback path. Recovery of in-flight
work itself is mediated entirely by the job lease: a crashed worker's `claimed`
job expires after 5 min and `JobReclaimHostedService` (30 s sweep) returns it to
`pending` for another worker.

Known gaps (triage separately, not yet closed): reclaim has no max-attempts cap;
a reclaimed *step* job re-runs its handler with no idempotency claim (at-least-once
— `non_reversible` steps re-apply, so lean on the compensation gate); and
`Complete/Fail` guard on job `Status` but not `ClaimedBy`.

## Failure modes and responses

| Failure | Detection | Response |
|---|---|---|
| WAL archive push failing | Postgres logs `archive command failed` | Page operations; archive_command failures back up WAL on disk and will eventually halt writes. |
| Object storage unreachable | pgbackrest exits non-zero in cron | Page operations; investigate IAM / network. Backups missing for > 24 h is a P1. |
| Restore drill exceeds RTO | Quarterly drill report | Open ticket, prioritise root cause for next quarter. |
| pgbackrest cipher pass lost | Restore fails with decrypt error | Backups are unrecoverable. Pass must be in the secret store with at least two break-glass accesses. |

## Backup of the keyring

Postgres holds the encrypted credentials, but the **DataProtection keyring**
(in `backend_keyring` Docker volume) holds the keys to decrypt them. Both
must be backed up together. Snapshot the volume in the same window as the
Postgres full backup. See [`docs/ops/keyring-rotation.md`](./keyring-rotation.md).

## See also

- [`docs/ops/branch-protection.md`](./branch-protection.md) — CI gates.
- [`docs/ops/keyring-rotation.md`](./keyring-rotation.md) — DataProtection keyring lifecycle.
