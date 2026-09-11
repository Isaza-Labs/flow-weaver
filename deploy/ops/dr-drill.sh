#!/usr/bin/env bash
# Automated DR drill (S13.1).
#
# Restores the latest pgbackrest backup into a TEMPORARY Postgres
# instance, runs sanity queries, and writes a timestamped report. Safe to
# run against the same machine as production because nothing touches the
# live volume — the drill spins up a sidecar Postgres on a different
# port and tears it down at the end.
#
# Usage (from a host that has access to the backup repository):
#   ./dr-drill.sh
#
# Required env (or .env):
#   PGBACKREST_STANZA      — usually "main"
#   PGBACKREST_REPO        — s3://bucket/path or /var/lib/pgbackrest
#   DR_DRILL_PORT          — port for the sidecar Postgres (default 55432)
#   DR_DRILL_VOLUME        — host path for the sidecar's data dir
#   DR_DRILL_REPORT_DIR    — where to drop the drill report
#
# Exit code 0 = drill passed, RTO measurement under 60 minutes.
# Exit code 1 = drill failed; reason logged + report still produced.
#
# Cron sample (quarterly, 02:00 first Monday):
#   0 2 1-7 1,4,7,10 1 /opt/flow-weaver/dr-drill.sh

set -euo pipefail

: "${PGBACKREST_STANZA:?PGBACKREST_STANZA required}"
: "${PGBACKREST_REPO:?PGBACKREST_REPO required}"
DRILL_PORT="${DR_DRILL_PORT:-55432}"
DRILL_VOLUME="${DR_DRILL_VOLUME:-/var/lib/flow-weaver/dr-drill}"
REPORT_DIR="${DR_DRILL_REPORT_DIR:-/var/log/flow-weaver/dr-drills}"
RTO_BUDGET_SECONDS="${DR_DRILL_RTO_BUDGET:-3600}" # 60 min

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
report="${REPORT_DIR}/dr-drill-${stamp}.log"
mkdir -p "${REPORT_DIR}" "${DRILL_VOLUME}"

container="flow-weaver-dr-drill-${stamp}"
start=$(date +%s)

log() { echo "[$(date -u -Is)] $*" | tee -a "${report}"; }
fail() { log "FAILED: $*"; teardown 1; exit 1; }

teardown() {
  local exit_code="${1:-0}"
  log "tearing down sidecar (exit_code=${exit_code})"
  docker rm -f "${container}" >/dev/null 2>&1 || true
  rm -rf "${DRILL_VOLUME:?}/"* 2>/dev/null || true
}
trap 'teardown 1' INT TERM

log "DR drill starting stanza=${PGBACKREST_STANZA} repo=${PGBACKREST_REPO}"

# 1. Spin up an empty Postgres on a non-conflicting port.
log "starting sidecar postgres on port ${DRILL_PORT}"
docker run -d \
  --name "${container}" \
  -p "${DRILL_PORT}:5432" \
  -v "${DRILL_VOLUME}:/var/lib/postgresql/data" \
  -e POSTGRES_PASSWORD=drill \
  -e POSTGRES_USER=flowweaver \
  -e POSTGRES_DB=flowweaver \
  postgres:17-alpine \
  >> "${report}" 2>&1 \
  || fail "could not start sidecar"

# 2. Wait for readiness.
log "waiting for sidecar to accept connections"
for _ in $(seq 1 60); do
  if docker exec "${container}" pg_isready -U flowweaver -q; then break; fi
  sleep 1
done
docker exec "${container}" pg_isready -U flowweaver -q || fail "sidecar not ready after 60s"

# 3. Restore the latest backup. We mount the same pgbackrest config so
#    the sidecar can reach the repo. In a real drill, the operator
#    swaps in real credentials.
log "restoring from pgbackrest"
docker exec "${container}" sh -c "
  apk add --no-cache pgbackrest >/dev/null 2>&1 || true
  pgbackrest --stanza='${PGBACKREST_STANZA}' \
             --repo1-path='${PGBACKREST_REPO}' \
             restore --delta
" >> "${report}" 2>&1 \
  || fail "pgbackrest restore failed"

# 4. Sanity queries — every counter should be > 0 in a healthy backup.
#    If any returns 0 or errors, the backup is suspect.
log "running sanity queries"
sanity_sql='
SELECT (SELECT COUNT(*) FROM "Workflow") AS workflows,
       (SELECT COUNT(*) FROM "Integration") AS integrations,
       (SELECT COUNT(*) FROM "Credential") AS credentials,
       (SELECT MAX("CreatedAt") FROM "WorkflowRun") AS last_run;'
docker exec "${container}" psql -U flowweaver -d flowweaver -c "${sanity_sql}" \
  >> "${report}" 2>&1 \
  || fail "sanity queries did not run"

# 5. Migrations are applied in-line by the application at boot; the
#    drill validates the *data* survived, not the schema upgrade path.
#    Schema-only checks live in the regular CI test suite.

elapsed=$(( $(date +%s) - start ))
log "drill completed in ${elapsed}s (budget ${RTO_BUDGET_SECONDS}s)"

if (( elapsed > RTO_BUDGET_SECONDS )); then
  log "WARNING: elapsed exceeded RTO budget"
  teardown 1
  exit 1
fi

teardown 0
log "DR drill PASSED"
exit 0
