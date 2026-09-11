#!/usr/bin/env bash
# Prepares a Linux host to run FlowWeaver's python_snippet sandbox.
#
# The sandbox (bubblewrap) needs to create a user namespace. Several distros
# restrict that by default, and each uses a different knob — which is where
# operators lose an afternoon. This script detects which one applies, applies
# the NARROWEST fix that works, and verifies it.
#
# Run once per host, as root:
#     sudo ./deploy/setup-host.sh
#
# Not the only option, and on a customer host the choice should be theirs:
# docs/ops/python-sandbox-host-requirements.md lays out all four (including
# not running python snippets at all, and a container-only route) with the
# security implications of each.
#
# Idempotent: safe to re-run, and it reports "already ok" rather than
# reapplying. Nothing here touches FlowWeaver itself — only host policy that
# bubblewrap depends on.

# Started as `sudo sh setup-host.sh`? That is dash on Debian/Ubuntu, which
# cannot run this script. Re-exec under bash transparently instead of failing
# with "Illegal option -o pipefail".
if [ -z "${BASH_VERSION:-}" ]; then
    command -v bash >/dev/null 2>&1 || { echo "Este script necesita bash." >&2; exit 1; }
    exec bash "$0" "$@"
fi

set -euo pipefail

log()  { printf '  %s\n' "$*"; }
ok()   { printf '\033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '\033[33m!\033[0m %s\n' "$*"; }
die()  { printf '\033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }

[[ ${EUID:-$(id -u)} -eq 0 ]] || die "ejecuta como root (sudo $0)"

echo "FlowWeaver — preparación del host para el sandbox de python_snippet"
echo

# ── 1. Is bubblewrap present? ────────────────────────────────────────────
# Only needed on the HOST for the verification below; the worker image ships
# its own. Absence is not fatal — we skip the smoke test and say so.
HAVE_BWRAP=0
command -v bwrap >/dev/null 2>&1 && HAVE_BWRAP=1

smoke_test() {
  [[ $HAVE_BWRAP -eq 1 ]] || return 2
  # Cheapest possible sandbox: create a user namespace and exit.
  bwrap --ro-bind / / --unshare-user true >/dev/null 2>&1
}

# The AUTHORITATIVE test runs INSIDE the worker container: that is the bwrap
# that python_snippet actually uses, and a fix that only helps the host binary
# would pass a host-only check while python steps keep failing. Measured on a
# real deployment: the AppArmor profile below path-matches /usr/bin/bwrap on
# the host, but the container's copy lives under an overlayfs path, so the
# profile does not attach to it — only the container test catches that.
CONTAINER=${FLOWWEAVER_WORKER_CONTAINER:-flow-weaver-worker}

smoke_test_container() {
  command -v docker >/dev/null 2>&1 || return 2
  [[ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" == true ]] || return 2
  docker exec -u app "$CONTAINER" \
    bwrap --ro-bind / / --unshare-user --unshare-pid --proc /proc true >/dev/null 2>&1
}

# Container verdict when available, host binary otherwise.
# 0 = works, 1 = fails, 2 = cannot test from here. Sets VERIFY_WHERE.
VERIFY_WHERE="host"
verify_now() {
  local st=0
  smoke_test_container || st=$?
  if [[ $st -ne 2 ]]; then VERIFY_WHERE="contenedor worker"; return $st; fi
  VERIFY_WHERE="host"
  smoke_test
}

verify_now && initial_status=0 || initial_status=$?

if [[ $initial_status -eq 0 ]]; then
  ok "los user namespaces sin privilegios ya funcionan (verificado en el $VERIFY_WHERE) — nada que hacer."
  exit 0
fi

# Unverifiable (status 2) is NOT evidence of a restriction — it means we cannot
# test from here. Say so, then fall through to detection anyway: the sysctls are
# readable regardless, and applying the right one is still useful. Conflating
# the two produced a confident "the smoke test failed" on hosts that simply do
# not have bubblewrap installed.
if [[ $initial_status -eq 2 ]]; then
  warn "bubblewrap no está instalado en este host y no hay ningún contenedor worker"
  log "($CONTAINER) corriendo, así que el sandbox no se puede verificar desde aquí."
  log "Se comprueban igualmente las restricciones del kernel."
  echo
fi

# ── 2. Which restriction is in play? ─────────────────────────────────────
APPARMOR_KNOB=kernel.apparmor_restrict_unprivileged_userns   # Ubuntu 23.10+
CLONE_KNOB=kernel.unprivileged_userns_clone                  # older Debian/Ubuntu
MAX_KNOB=user.max_user_namespaces                            # RHEL and derivatives

get() { sysctl -n "$1" 2>/dev/null || true; }

APPARMOR_VAL=$(get "$APPARMOR_KNOB")
CLONE_VAL=$(get "$CLONE_KNOB")
MAX_VAL=$(get "$MAX_KNOB")

applied=0

# ── 2a. Ubuntu 23.10+ — AppArmor restriction ─────────────────────────────
if [[ "$APPARMOR_VAL" == "1" ]]; then
  log "detectado: $APPARMOR_KNOB=1 (restricción AppArmor de Ubuntu 23.10+)"

  # Preferred: grant `userns` to bwrap ALONE and leave the system-wide
  # hardening in place. Turning the sysctl off would lift the restriction for
  # every binary on the box, which is a much larger change than this needs.
  if command -v apparmor_parser >/dev/null 2>&1 && [[ -d /etc/apparmor.d ]]; then
    BWRAP_PATH=$(command -v bwrap || echo /usr/bin/bwrap)
    if [[ ! -f /etc/apparmor.d/bwrap ]]; then
      cat > /etc/apparmor.d/bwrap <<EOF
# Installed by FlowWeaver deploy/setup-host.sh.
# Grants user-namespace creation to bubblewrap only; every other binary stays
# under Ubuntu's default restriction.
abi <abi/4.0>,
include <tunables/global>

profile bwrap $BWRAP_PATH flags=(unconfined) {
  userns,
  include if exists <local/bwrap>
}
EOF
      log "escrito /etc/apparmor.d/bwrap"
    else
      log "/etc/apparmor.d/bwrap ya existía"
    fi

    if systemctl reload apparmor >/dev/null 2>&1 \
       || apparmor_parser -r /etc/apparmor.d/bwrap >/dev/null 2>&1; then
      applied=1
      log "apparmor recargado"
    else
      warn "no se pudo recargar apparmor — se usará el sysctl como alternativa"
    fi
  fi

  # Fallback: relax it globally. Less targeted, but it is the documented
  # remedy, some hosts have no apparmor_parser, and the profile above only
  # covers the HOST's bwrap — when the worker container is running, verify_now
  # tests the container's own bwrap and lands here if the profile fell short.
  if [[ $applied -eq 0 ]] || ! verify_now; then
    echo "$APPARMOR_KNOB=0" > /etc/sysctl.d/99-flowweaver-userns.conf
    sysctl --system >/dev/null 2>&1 || sysctl -w "$APPARMOR_KNOB=0" >/dev/null
    log "escrito /etc/sysctl.d/99-flowweaver-userns.conf ($APPARMOR_KNOB=0)"
    applied=1
  fi

# ── 2b. Older Debian / Ubuntu ────────────────────────────────────────────
elif [[ "$CLONE_VAL" == "0" ]]; then
  log "detectado: $CLONE_KNOB=0 (Debian/Ubuntu antiguos)"
  echo "$CLONE_KNOB=1" > /etc/sysctl.d/99-flowweaver-userns.conf
  sysctl --system >/dev/null 2>&1 || sysctl -w "$CLONE_KNOB=1" >/dev/null
  applied=1

# ── 2c. RHEL and derivatives ─────────────────────────────────────────────
elif [[ -n "$MAX_VAL" && "$MAX_VAL" -eq 0 ]]; then
  log "detectado: $MAX_KNOB=0 (familia RHEL)"
  echo "$MAX_KNOB=15000" > /etc/sysctl.d/99-flowweaver-userns.conf
  sysctl --system >/dev/null 2>&1 || sysctl -w "$MAX_KNOB=15000" >/dev/null
  applied=1
fi

# ── 3. Verify ────────────────────────────────────────────────────────────
echo
if [[ $applied -eq 0 && $initial_status -eq 2 ]]; then
  ok "no se encontró ninguna restricción del kernel sobre user namespaces en este host."
  log "Nada que cambiar. Verifícalo desde dentro del worker, que trae bubblewrap:"
  log "docker exec -u app <worker> bwrap --ro-bind / / --unshare-user true"
  exit 0
fi

if [[ $applied -eq 0 ]]; then
  warn "ninguna restricción conocida de user namespaces coincide y aun así la prueba falla."
  log "Reporta estos valores para que la detección se pueda extender:"
  log "  $APPARMOR_KNOB = ${APPARMOR_VAL:-<absent>}"
  log "  $CLONE_KNOB = ${CLONE_VAL:-<absent>}"
  log "  $MAX_KNOB = ${MAX_VAL:-<absent>}"
  log "  kernel: $(uname -r)"
  exit 1
fi

# Captured explicitly: reading $? inside the elif would depend on nothing else
# running in between, which is the kind of thing a later edit breaks silently.
# 0 = works, 2 = unverifiable from here, anything else = still failing.
verify_now && smoke_status=0 || smoke_status=$?

if [[ $smoke_status -eq 0 ]]; then
  ok "los user namespaces sin privilegios ya funcionan (verificado en el $VERIFY_WHERE). Reinicia el worker:"
  log "docker compose -f deploy/docker-compose.yml restart worker"
elif [[ $smoke_status -eq 2 ]]; then
  warn "aplicado, pero no hay ni contenedor worker corriendo ni bubblewrap en el host"
  log "para verificarlo desde aquí. Compruébalo dentro del contenedor cuando arranque:"
  log "docker exec -u app $CONTAINER bwrap --ro-bind / / --unshare-user true"
else
  die "el fix se aplicó pero el sandbox sigue fallando — mira docs/handlers/python_snippet.md"
fi
