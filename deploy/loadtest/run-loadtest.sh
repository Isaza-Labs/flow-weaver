#!/usr/bin/env bash
# Device-scaling load test.
#
# Drives a STAGING FlowWeaver (with Simulation:Enabled=true) through a series of
# device counts, firing one representative per-device ping fan-out run at each
# step and recording the run-level p95 / throughput / error-rate from the SLO
# endpoint. Fakes all device I/O — no real hardware is touched.
#
# PREREQUISITES
#   - A staging deploy with SIMULATION_ENABLED=true and WORKER_ENVIRONMENT != production
#     (docker compose -f deploy/docker-compose.yml up -d, with the flag set in .env).
#   - curl + jq on the machine running this script.
#
# USAGE
#   BASE_URL=http://localhost:8080 ADMIN_USER=admin ADMIN_PASS=admin \
#     COUNTS="50 250 1000 5000" ./run-loadtest.sh
#
# Env:
#   BASE_URL     backend base url (default http://localhost:8080)
#   ADMIN_USER / ADMIN_PASS   admin credentials to mint a JWT (or set TOKEN directly)
#   TOKEN        pre-minted JWT (skips login)
#   COUNTS       space-separated device counts (default "50 250 1000 5000")
#   RUN_TIMEOUT  seconds to wait for a run to finish (default 900)
#   REPORT_DIR   where to write the CSV report (default ./loadtest-reports)

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
COUNTS="${COUNTS:-50 250 1000 5000}"
RUN_TIMEOUT="${RUN_TIMEOUT:-900}"
REPORT_DIR="${REPORT_DIR:-./loadtest-reports}"

command -v jq >/dev/null || { echo "jq is required"; exit 1; }
mkdir -p "$REPORT_DIR"

api() { # method path [json-body]
  local method="$1" path="$2" body="${3:-}"
  if [[ -n "$body" ]]; then
    curl -fsS -X "$method" "$BASE_URL$path" \
      -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$body"
  else
    curl -fsS -X "$method" "$BASE_URL$path" -H "Authorization: Bearer $TOKEN"
  fi
}

# 1. Auth.
if [[ -z "${TOKEN:-}" ]]; then
  echo "Logging in as ${ADMIN_USER:-admin}…"
  TOKEN="$(curl -fsS -X POST "$BASE_URL/api/auth/login" -H "Content-Type: application/json" \
    -d "{\"username\":\"${ADMIN_USER:-admin}\",\"password\":\"${ADMIN_PASS:-admin}\"}" | jq -r '.access_token // .token')"
  [[ -n "$TOKEN" && "$TOKEN" != "null" ]] || { echo "login failed"; exit 1; }
fi

report="$REPORT_DIR/loadtest-$(date -u +%Y%m%dT%H%M%SZ).csv"
echo "device_count,run_id,run_status,steps,run_seconds,slo_p95_seconds,slo_error_rate,slo_throughput_per_hour" > "$report"
echo "Report: $report"

for count in $COUNTS; do
  echo "─── $count devices ───────────────────────────────────────────"
  api DELETE /api/admin/loadtest/teardown >/dev/null || true

  seed="$(api POST /api/admin/loadtest/seed "{\"count\":$count}")"
  wf="$(echo "$seed" | jq -r '.workflow_id')"
  pool="$(echo "$seed" | jq -r '.pool_id')"
  echo "  seeded: workflow=$wf pool=$pool"

  run="$(api POST "/api/workflow/$wf/run" "{\"target_pools\":[\"$pool\"]}")"
  run_id="$(echo "$run" | jq -r '.id // .workflow_run_id')"
  echo "  run: $run_id — waiting (timeout ${RUN_TIMEOUT}s)…"

  start=$(date +%s); status="pending"
  while (( $(date +%s) - start < RUN_TIMEOUT )); do
    status="$(api GET "/api/run/$run_id" | jq -r '.status')"
    [[ "$status" == "completed" || "$status" == "failed" || "$status" == "cancelled" ]] && break
    sleep 3
  done
  elapsed=$(( $(date +%s) - start ))

  steps="$(api GET "/api/run/$run_id/steps" | jq 'length')"
  slo="$(api GET '/api/admin/metrics/slo')"
  p95="$(echo "$slo" | jq -r '.run_latency_p95_seconds // .p95 // "n/a"')"
  err="$(echo "$slo" | jq -r '.error_rate // "n/a"')"
  thr="$(echo "$slo" | jq -r '.throughput_jobs_per_hour // .throughput // "n/a"')"

  echo "  status=$status steps=$steps run_seconds=$elapsed p95=$p95 error_rate=$err throughput/h=$thr"
  echo "$count,$run_id,$status,$steps,$elapsed,$p95,$err,$thr" >> "$report"
done

api DELETE /api/admin/loadtest/teardown >/dev/null || true
echo "Done. Curves in $report — plot device_count vs run_seconds / p95 / error_rate to find the saturation knee."
