#!/usr/bin/env bash
# flow-weaver launcher (Linux / macOS). Windows: use run.ps1 — same flags.
#
# Brings up backend + worker + frontend, with the database either managed by
# this compose project or supplied externally. Every port and setting can be
# chosen on the command line; anything you leave out is asked for (or taken
# from deploy/.env, or defaulted) — pass -y to never prompt.
#
#   ./run.sh                                  # interactive
#   ./run.sh -y                               # defaults, no questions
#   ./run.sh -y --db external --db-host host.docker.internal --db-password s3cr3t
#   ./run.sh -y --backend-port 8090 --frontend-port 3001
#   ./run.sh logs backend
#   ./run.sh down
#
# Settings are persisted to deploy/.env, so the next run reuses them.

# Started as `sh run.sh`? That is dash on Debian/Ubuntu, which cannot run
# this script (`set -o pipefail`, arrays…). Re-exec under bash transparently
# instead of failing on the next line with "Illegal option -o pipefail".
if [ -z "${BASH_VERSION:-}" ]; then
    command -v bash >/dev/null 2>&1 || { echo "This script requires bash." >&2; exit 1; }
    exec bash "$0" "$@"
fi

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

ENV_FILE=".env"
COMMAND="up"
ASSUME_YES=0
DO_BUILD=""          # "", "yes", "no"
DO_PULL=0
DRY_RUN=0
TUNNEL=0
DEV_OVERRIDES="auto" # auto | yes | no
SERVICES=""

# Values resolved from flags > .env > default.
DB_MODE=""; DB_HOST=""; DB_PORT=""; DB_NAME=""; DB_USER=""; DB_PASSWORD=""
BACKEND_PORT=""; FRONTEND_PORT=""; POSTGRES_PORT=""
APP_ENV=""; SUBNET=""; FRONTEND_ORIGIN=""

RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; BOLD=$'\033[1m'; OFF=$'\033[0m'
say()  { printf '%s\n' "$*"; }
info() { printf '%s%s%s\n' "$BOLD" "$*" "$OFF"; }
warn() { printf '%s%s%s\n' "$YELLOW" "$*" "$OFF" >&2; }
die()  { printf '%s%s%s\n' "$RED" "$*" "$OFF" >&2; exit 1; }

usage() {
    sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<'EOF'

Commands:
  up (default)   start the stack (builds images the first time)
  down           stop and remove containers
  restart        down + up
  logs [svc]     follow logs (all services, or one)
  ps             show container status
  config         print the fully resolved compose configuration

Options:
  -y, --yes                 never prompt; use flags, then .env, then defaults
      --db MODE             docker (bundled Postgres) | external
      --db-host HOST        external DB host  ("localhost" is rewritten to
                            host.docker.internal, which is what a container needs)
      --db-port PORT        external DB port (default 5432)
      --db-name NAME        database name (default flowweaver)
      --db-user USER        database user (default flowweaver)
      --db-password PASS    database password
      --backend-port N      published backend port (default 8080)
      --frontend-port N     published frontend port (default 3000)
      --postgres-port N     published port of the bundled Postgres (default 5432)
      --env NAME            ASPNETCORE_ENVIRONMENT (default Production)
      --subnet CIDR         compose network subnet; default picks a free /16
      --services "a b c"    only these services (default: all applicable)
      --tunnel              also start cloudflared (public webhook ingress)
      --dev-overrides       force-include docker-compose.override.yml
      --no-dev-overrides    ignore it (default: include only if shared-net exists)
      --build / --no-build  force or skip the image build
      --pull                pull base images before building
      --dry-run             resolve everything and print the command, run nothing
  -h, --help                this text
EOF
}

# ── .env helpers ────────────────────────────────────────────────────────────
env_get() {
    [[ -f $ENV_FILE ]] || return 0
    local raw
    raw=$(sed -n "s/^$1=//p" "$ENV_FILE" | tail -1)
    # Stored form escapes $ as $$ (see env_set); undo it so callers always work
    # with the logical value. Without this, re-reading and re-writing would
    # double the escaping on every run and silently corrupt passwords.
    printf '%s' "${raw//\$\$/\$}"
}

env_set() {
    local key=$1 value=$2
    # Compose interpolates $ in .env values; $$ is the literal escape.
    value=${value//$/\$\$}
    touch "$ENV_FILE"
    if grep -q "^$key=" "$ENV_FILE"; then
        local tmp; tmp=$(mktemp)
        awk -v k="$key" -v v="$value" \
            'BEGIN{FS=OFS="="} $1==k && !done {print k "=" v; done=1; next} {print}' \
            "$ENV_FILE" > "$tmp"
        mv "$tmp" "$ENV_FILE"
    else
        printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
    fi
}

# flag value, else .env, else default
resolve() {
    local current=$1 key=$2 default=$3 from_env
    if [[ -n $current ]]; then printf '%s' "$current"; return; fi
    from_env=$(env_get "$key")
    printf '%s' "${from_env:-$default}"
}

ask() {
    local prompt=$1 default=$2 answer
    if (( ASSUME_YES )); then printf '%s' "$default"; return; fi
    read -r -p "$prompt [$default]: " answer </dev/tty || true
    printf '%s' "${answer:-$default}"
}

ask_secret() {
    local prompt=$1 default=$2 answer
    if (( ASSUME_YES )); then printf '%s' "$default"; return; fi
    read -r -s -p "$prompt${default:+ [unchanged]}: " answer </dev/tty || true
    printf '\n' >&2
    printf '%s' "${answer:-$default}"
}

# ── environment probing ─────────────────────────────────────────────────────
docker_ok() {
    command -v docker >/dev/null 2>&1 || die "docker not found on PATH."
    docker compose version >/dev/null 2>&1 \
        || die "'docker compose' (v2) not available. Install the Compose plugin."
    # A dry run only resolves configuration, so it works without a live daemon.
    (( DRY_RUN )) && return 0
    docker info >/dev/null 2>&1 || die "Cannot talk to the Docker daemon. Is it running?"
}

used_subnets() {
    docker network ls -q 2>/dev/null | while read -r id; do
        docker network inspect "$id" \
            --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}' 2>/dev/null
    done
}

# Picks a /16 nobody is using. Sticks to 172.16–172.31 (Docker's own default
# pool) rather than 10.x or 192.168.x, which on a network-automation host are
# usually the device management ranges — a Docker subnet overlapping those
# would blackhole traffic to the very equipment this platform drives.
pick_free_subnet() {
    local used i candidate
    used=$(used_subnets || true)
    for i in $(seq 16 31); do
        candidate="172.$i.0.0/16"
        grep -q "^172\.$i\." <<<"$(tr ' ' '\n' <<<"$used")" || { printf '%s' "$candidate"; return; }
    done
    warn "No free /16 left in 172.16-172.31; falling back to 10.199.0.0/16."
    printf '%s' "10.199.0.0/16"
}

subnet_is_free() {
    local want=${1%%/*} used
    used=$(tr ' ' '\n' <<<"$(used_subnets || true)")
    ! grep -q "^${want%.*.*}\." <<<"$used"
}

port_busy() {
    local port=$1
    if command -v ss >/dev/null 2>&1; then
        ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]$port\$"
    elif command -v lsof >/dev/null 2>&1; then
        lsof -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1
    else
        return 1
    fi
}

tcp_reachable() {
    local host=$1 port=$2
    timeout 3 bash -c "exec 3<>/dev/tcp/$host/$port" 2>/dev/null
}

gen_jwt_key() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 48 | tr -d '\n'
    else
        head -c 48 /dev/urandom | base64 | tr -d '\n'
    fi
}

# python_snippet runs inside bubblewrap, which needs unprivileged user
# namespaces. Several distros restrict those on the HOST (Ubuntu 23.10+,
# RHEL, older Debian) and the compose relaxations cannot override host
# policy — so after `up` we test the REAL path: bwrap inside the worker,
# as the same non-root user, including the fresh /proc mount. Failing here
# is exactly the failure a python step would hit later, but surfaced now,
# with the fix named.
verify_sandbox() {
    # Only meaningful when the worker service was started.
    if [[ -n $SERVICES && " $SERVICES " != *" worker "* ]]; then return 0; fi

    local i running=""
    for i in $(seq 1 15); do
        running=$(docker inspect -f '{{.State.Running}}' flow-weaver-worker 2>/dev/null || true)
        [[ $running == true ]] && break
        sleep 2
    done
    if [[ $running != true ]]; then
        warn "No se pudo verificar el sandbox de python_snippet: el worker no está corriendo."
        warn "Revisa: ./run.sh logs worker"
        return 0
    fi

    if docker exec -u app flow-weaver-worker \
        bwrap --ro-bind / / --unshare-user --unshare-pid --proc /proc true >/dev/null 2>&1; then
        say "  ${GREEN}✓${OFF} Sandbox de python_snippet verificado (bwrap + user namespaces)."
        return 0
    fi

    warn ""
    warn "✗ El sandbox de python_snippet NO funciona en este host: los pasos python"
    warn "  fallarán (p. ej. 'bwrap: setting up uid map: Permission denied')."
    warn "  El resto de la plataforma no se ve afectada."
    if [[ $(uname -s) == Linux ]]; then
        warn "Causa habitual: el host restringe los user namespaces sin privilegios"
        warn "(Ubuntu 23.10+, RHEL, Debian antiguo). Arréglalo una sola vez con:"
        warn "  sudo bash $PWD/setup-host.sh"
        warn "y después reinicia el worker:"
        warn "  ./run.sh restart"
    fi
    warn "Opciones y sus implicaciones: docs/ops/python-sandbox-host-requirements.md"
}

# ── argument parsing ────────────────────────────────────────────────────────
case "${1:-}" in
    up|down|restart|logs|ps|config) COMMAND=$1; shift ;;
    -h|--help) usage; exit 0 ;;
esac

LOG_SERVICE=""
while (( $# )); do
    case $1 in
        -y|--yes)           ASSUME_YES=1 ;;
        --db)               DB_MODE=${2:?}; shift ;;
        --db-host)          DB_HOST=${2:?}; shift ;;
        --db-port)          DB_PORT=${2:?}; shift ;;
        --db-name)          DB_NAME=${2:?}; shift ;;
        --db-user)          DB_USER=${2:?}; shift ;;
        --db-password)      DB_PASSWORD=${2:?}; shift ;;
        --backend-port)     BACKEND_PORT=${2:?}; shift ;;
        --frontend-port)    FRONTEND_PORT=${2:?}; shift ;;
        --postgres-port)    POSTGRES_PORT=${2:?}; shift ;;
        --env)              APP_ENV=${2:?}; shift ;;
        --subnet)           SUBNET=${2:?}; shift ;;
        --services)         SERVICES=${2:?}; shift ;;
        --tunnel)           TUNNEL=1 ;;
        --dev-overrides)    DEV_OVERRIDES=yes ;;
        --no-dev-overrides) DEV_OVERRIDES=no ;;
        --build)            DO_BUILD=yes ;;
        --no-build)         DO_BUILD=no ;;
        --pull)             DO_PULL=1 ;;
        --dry-run)          DRY_RUN=1 ;;
        -h|--help)          usage; exit 0 ;;
        -*)                 die "Unknown option: $1 (see --help)" ;;
        *)                  LOG_SERVICE=$1 ;;
    esac
    shift
done

if [[ ! -t 0 && ! -r /dev/tty ]] && (( ! ASSUME_YES )); then
    ASSUME_YES=1
    warn "Sin terminal interactiva: se asume -y (defaults + flags)."
fi

docker_ok

# ── simple commands need no configuration ───────────────────────────────────
compose_files() {
    local files=(-f docker-compose.yml)
    [[ $(env_get DB_MODE) == external ]] && files+=(-f compose.external-db.yml)
    local include_dev=$DEV_OVERRIDES
    if [[ $include_dev == auto ]]; then
        if docker network inspect shared-net >/dev/null 2>&1; then include_dev=yes; else include_dev=no; fi
    fi
    [[ $include_dev == yes ]] && files+=(-f docker-compose.override.yml)
    printf '%s\n' "${files[@]}"
}

mapfile -t FILES < <(compose_files)

case $COMMAND in
    down)
        info "Stopping flow-weaver…"
        docker compose "${FILES[@]}" down --remove-orphans
        exit 0 ;;
    logs)
        exec docker compose "${FILES[@]}" logs -f --tail=200 ${LOG_SERVICE:+"$LOG_SERVICE"} ;;
    ps)
        exec docker compose "${FILES[@]}" ps ;;
    config)
        exec docker compose "${FILES[@]}" config ;;
esac

# ── gather configuration ────────────────────────────────────────────────────
info "flow-weaver — configuración"
say  "Los valores entre corchetes son los actuales (deploy/.env) o el default."
say  ""

DB_MODE=$(resolve "$DB_MODE" DB_MODE docker)
if (( ! ASSUME_YES )); then
    DB_MODE=$(ask "Base de datos: 'docker' (gestionada aquí) o 'external'" "$DB_MODE")
fi
[[ $DB_MODE == docker || $DB_MODE == external ]] || die "--db must be 'docker' or 'external'."

DB_NAME=$(resolve "$DB_NAME" POSTGRES_DB flowweaver)
DB_USER=$(resolve "$DB_USER" POSTGRES_USER flowweaver)
DB_PASSWORD=$(resolve "$DB_PASSWORD" POSTGRES_PASSWORD "")

if [[ $DB_MODE == docker ]]; then
    POSTGRES_PORT=$(resolve "$POSTGRES_PORT" POSTGRES_PORT 5432)
    if (( ! ASSUME_YES )); then
        DB_NAME=$(ask "  Nombre de la base de datos" "$DB_NAME")
        DB_USER=$(ask "  Usuario" "$DB_USER")
        DB_PASSWORD=$(ask_secret "  Contraseña" "$DB_PASSWORD")
        POSTGRES_PORT=$(ask "  Puerto publicado de Postgres" "$POSTGRES_PORT")
    fi
    [[ -n $DB_PASSWORD ]] || DB_PASSWORD=flowweaver
    DB_HOST=db; DB_PORT=5432
    port_busy "$POSTGRES_PORT" && warn "Aviso: el puerto $POSTGRES_PORT ya está escuchando en el host."
else
    DB_HOST=$(resolve "$DB_HOST" DB_HOST host.docker.internal)
    DB_PORT=$(resolve "$DB_PORT" DB_PORT 5432)
    if (( ! ASSUME_YES )); then
        DB_HOST=$(ask "  Host de la base de datos" "$DB_HOST")
        DB_PORT=$(ask "  Puerto" "$DB_PORT")
        DB_NAME=$(ask "  Nombre de la base de datos" "$DB_NAME")
        DB_USER=$(ask "  Usuario" "$DB_USER")
        DB_PASSWORD=$(ask_secret "  Contraseña" "$DB_PASSWORD")
    fi
    [[ -n $DB_PASSWORD ]] || die "La contraseña de la base de datos externa es obligatoria (--db-password)."

    # From inside a container, localhost is the container itself.
    if [[ $DB_HOST == localhost || $DB_HOST == 127.0.0.1 || $DB_HOST == ::1 ]]; then
        warn "'$DB_HOST' apunta al propio contenedor; se usará host.docker.internal."
        DB_HOST=host.docker.internal
    fi

    probe_host=$DB_HOST
    [[ $probe_host == host.docker.internal ]] && probe_host=127.0.0.1
    if tcp_reachable "$probe_host" "$DB_PORT"; then
        say "  ${GREEN}✓${OFF} $probe_host:$DB_PORT responde."
    else
        warn "  No se pudo conectar a $probe_host:$DB_PORT desde el host."
        warn "  Si la base sólo escucha dentro de otra red Docker, se seguirá intentando al arrancar."
    fi
fi

BACKEND_PORT=$(resolve "$BACKEND_PORT" BACKEND_PORT 8080)
FRONTEND_PORT=$(resolve "$FRONTEND_PORT" FRONTEND_PORT 3000)
APP_ENV=$(resolve "$APP_ENV" ASPNETCORE_ENVIRONMENT Production)
if (( ! ASSUME_YES )); then
    say ""
    BACKEND_PORT=$(ask "Puerto del backend" "$BACKEND_PORT")
    FRONTEND_PORT=$(ask "Puerto del frontend" "$FRONTEND_PORT")
    APP_ENV=$(ask "Entorno (Production/Development)" "$APP_ENV")
fi
port_busy "$BACKEND_PORT"  && warn "Aviso: el puerto $BACKEND_PORT ya está escuchando en el host."
port_busy "$FRONTEND_PORT" && warn "Aviso: el puerto $FRONTEND_PORT ya está escuchando en el host."

# The frontend's ORIGIN must match the URL the browser actually uses, or
# SvelteKit rejects form posts as cross-origin.
FRONTEND_ORIGIN=$(env_get FRONTEND_ORIGIN)
default_origin="http://localhost:$FRONTEND_PORT"
if [[ -z $FRONTEND_ORIGIN || $FRONTEND_ORIGIN == http://localhost:* ]]; then
    FRONTEND_ORIGIN=$default_origin
fi
(( ASSUME_YES )) || FRONTEND_ORIGIN=$(ask "URL pública del frontend (ORIGIN)" "$FRONTEND_ORIGIN")

# Subnet: reuse what is stored if it is still free, otherwise find a gap.
SUBNET=$(resolve "$SUBNET" COMPOSE_SUBNET "")
if [[ -z $SUBNET ]]; then
    SUBNET=$(pick_free_subnet)
    say "Subred libre elegida automáticamente: $SUBNET"
elif ! subnet_is_free "$SUBNET"; then
    if docker network inspect flow-weaver_flow-weaver-net >/dev/null 2>&1; then
        : # it is ours, already created with this range
    else
        warn "La subred $SUBNET está ocupada por otra red Docker."
        SUBNET=$(pick_free_subnet)
        warn "Se usará $SUBNET en su lugar."
    fi
fi

JWT_KEY=$(env_get JWT_KEY)
if [[ -z $JWT_KEY ]]; then
    JWT_KEY=$(gen_jwt_key)
    say "JWT_KEY generada (48 bytes aleatorios) y guardada en deploy/.env."
fi

# ── persist ─────────────────────────────────────────────────────────────────
env_set DB_MODE                "$DB_MODE"
env_set DB_HOST                "$DB_HOST"
env_set DB_PORT                "$DB_PORT"
env_set POSTGRES_DB            "$DB_NAME"
env_set POSTGRES_USER          "$DB_USER"
env_set POSTGRES_PASSWORD      "$DB_PASSWORD"
env_set BACKEND_PORT           "$BACKEND_PORT"
env_set FRONTEND_PORT          "$FRONTEND_PORT"
env_set FRONTEND_ORIGIN        "$FRONTEND_ORIGIN"
env_set ASPNETCORE_ENVIRONMENT "$APP_ENV"
env_set COMPOSE_SUBNET         "$SUBNET"
env_set TRUSTED_PROXIES        "$SUBNET"   # must track the subnet, see docker-compose.yml
env_set JWT_KEY                "$JWT_KEY"
[[ $DB_MODE == docker ]] && env_set POSTGRES_PORT "$POSTGRES_PORT"

# ── build the command ───────────────────────────────────────────────────────
mapfile -t FILES < <(compose_files)

if [[ -z $SERVICES ]]; then
    if [[ $DB_MODE == external ]]; then SERVICES="backend worker frontend"; fi
fi
read -r -a SERVICE_ARR <<<"${SERVICES:-}"

UP_ARGS=(up -d --remove-orphans)
(( TUNNEL )) && FILES+=(--profile tunnel)

case $DO_BUILD in
    yes) UP_ARGS+=(--build) ;;
    no)  ;;
    *)   docker image inspect flow-weaver-backend:latest >/dev/null 2>&1 || UP_ARGS+=(--build) ;;
esac

say ""
info "Resumen"
say "  Base de datos   : $DB_MODE ($DB_HOST:$DB_PORT/$DB_NAME como $DB_USER)"
say "  Backend         : http://localhost:$BACKEND_PORT"
say "  Frontend        : $FRONTEND_ORIGIN (puerto $FRONTEND_PORT)"
say "  Entorno         : $APP_ENV"
say "  Subred          : $SUBNET (TRUSTED_PROXIES en sincronía)"
say "  Servicios       : ${SERVICES:-todos}"
say "  Compose         : ${FILES[*]}"
say ""

if (( DRY_RUN )); then
    say "${YELLOW}--dry-run${OFF}: no se ejecuta nada. El comando sería:"
    say "  docker compose ${FILES[*]} ${UP_ARGS[*]} ${SERVICES:-}"
    exit 0
fi

if (( ! ASSUME_YES )); then
    confirm=$(ask "¿Arrancar ahora? (s/n)" "s")
    [[ $confirm =~ ^[sSyY] ]] || { say "Cancelado. La configuración quedó guardada en deploy/.env."; exit 0; }
fi

(( DO_PULL )) && docker compose "${FILES[@]}" pull --ignore-buildable || true

if [[ $COMMAND == restart ]]; then
    docker compose "${FILES[@]}" down --remove-orphans || true
fi

docker compose "${FILES[@]}" "${UP_ARGS[@]}" ${SERVICE_ARR[@]+"${SERVICE_ARR[@]}"}

say ""
verify_sandbox

say ""
say "${GREEN}Listo.${OFF}"
say "  Frontend : $FRONTEND_ORIGIN"
say "  API      : http://localhost:$BACKEND_PORT/api/v1"
say "  Logs     : ./run.sh logs backend"
say ""
say "Las migraciones se aplican solas al arrancar el backend; compruébalo con:"
say "  ./run.sh logs backend | grep -i 'migrations applied'"
