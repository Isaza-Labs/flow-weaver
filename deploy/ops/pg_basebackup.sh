#!/usr/bin/env bash
# Fallback Postgres backup script for environments without pgbackrest.
# Performs a full pg_basebackup into a timestamped directory and rotates
# anything older than BACKUP_RETENTION_DAYS. WAL archiving must be
# configured separately for RPO < 24h.
#
# Required env:
#   PGHOST, PGPORT, PGUSER, PGPASSWORD
#   BACKUP_DIR             — destination directory (mounted volume)
#   BACKUP_RETENTION_DAYS  — defaults to 30
#
# Recommended cron (in the db host or a sidecar):
#   0 2 * * * /opt/flow-weaver/pg_basebackup.sh >> /var/log/fw-backup.log 2>&1

set -euo pipefail

: "${PGHOST:?PGHOST required}"
: "${PGUSER:?PGUSER required}"
: "${BACKUP_DIR:?BACKUP_DIR required}"
PGPORT="${PGPORT:-5432}"
RETENTION="${BACKUP_RETENTION_DAYS:-30}"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="${BACKUP_DIR}/base-${stamp}"
mkdir -p "${target}"

echo "[$(date -u -Is)] starting pg_basebackup -> ${target}"
pg_basebackup \
  -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" \
  -D "${target}" \
  -F tar -z \
  -X stream \
  -P -v

echo "[$(date -u -Is)] pruning backups older than ${RETENTION} days"
find "${BACKUP_DIR}" -mindepth 1 -maxdepth 1 -type d -name 'base-*' \
  -mtime "+${RETENTION}" -print -exec rm -rf {} +

echo "[$(date -u -Is)] backup complete"
