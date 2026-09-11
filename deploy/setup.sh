#!/usr/bin/env bash
# flow-weaver first-run wizard (Linux / macOS). Windows: setup.ps1 — same flow.
#
# One command from zero to a working instance:
#   1. Deploy      — with Docker (delegates to run.sh, which configures
#                    deploy/.env interactively) or natively (dotnet + node
#                    against an existing Postgres), or attach to an already
#                    running backend.
#   2. Credentials — creates the initial admin account (your username and
#                    password, or a generated one-time password).
#   3. AI provider — registers OpenAI / Anthropic / Gemini / Ollama and runs
#                    a live connectivity test.
#   4. AI agent    — creates the default "assistant" agent wired to that
#                    provider so /ai/chat works immediately.
#
#   ./setup.sh              # full wizard
#   ./setup.sh configure    # skip deploy, only bootstrap an existing backend
#   ./setup.sh stop         # stop a native (non-Docker) deployment
#
# Safe to re-run: every step detects existing state and offers to skip.

# Started as `sh setup.sh`? That is dash on Debian/Ubuntu, which cannot run
# this script (`set -o pipefail`, arrays…). Re-exec under bash transparently
# instead of failing on the next line with "Illegal option -o pipefail".
if [ -z "${BASH_VERSION:-}" ]; then
    command -v bash >/dev/null 2>&1 || { echo "This script requires bash." >&2; exit 1; }
    exec bash "$0" "$@"
fi

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

ENV_FILE=".env"
NATIVE_DIR="native"
COMMAND="${1:-}"

RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; BOLD=$'\033[1m'; OFF=$'\033[0m'
say()  { printf '%s\n' "$*"; }
info() { printf '\n%s%s%s\n' "$BOLD" "$*" "$OFF"; }
ok()   { printf '%s✓%s %s\n' "$GREEN" "$OFF" "$*"; }
warn() { printf '%s%s%s\n' "$YELLOW" "$*" "$OFF" >&2; }
die()  { printf '%s%s%s\n' "$RED" "$*" "$OFF" >&2; exit 1; }

ask() {
    local prompt=$1 default=${2:-} answer
    read -r -p "$prompt${default:+ [$default]}: " answer </dev/tty || true
    printf '%s' "${answer:-$default}"
}

ask_secret() {
    local prompt=$1 answer
    read -r -s -p "$prompt: " answer </dev/tty || true
    printf '\n' >&2
    printf '%s' "$answer"
}

# Minimal .env helpers (same $$-escaping contract as run.sh).
env_get() {
    [[ -f $ENV_FILE ]] || return 0
    local raw; raw=$(sed -n "s/^$1=//p" "$ENV_FILE" | tail -1)
    printf '%s' "${raw//\$\$/\$}"
}
env_set() {
    local key=$1 value=$2
    value=${value//$/\$\$}
    touch "$ENV_FILE"
    if grep -q "^$key=" "$ENV_FILE"; then
        local tmp; tmp=$(mktemp)
        awk -v k="$key" -v v="$value" 'BEGIN{FS=OFS="="} $1==k && !done {print k "=" v; done=1; next} {print}' \
            "$ENV_FILE" > "$tmp" && mv "$tmp" "$ENV_FILE"
    else
        printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
    fi
}

# Tiny JSON string-field extractor — enough for the fields this wizard reads,
# no jq dependency. Usage: json_get '<json>' field_name
json_get() {
    printf '%s' "$1" | sed -n 's/.*"'"$2"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
}

gen_key() {
    if command -v openssl >/dev/null 2>&1; then openssl rand -base64 48 | tr -d '\n'
    else head -c 48 /dev/urandom | base64 | tr -d '\n'; fi
}

command -v curl >/dev/null 2>&1 || die "curl is required."

# ── stop (native mode) ──────────────────────────────────────────────────────
if [[ $COMMAND == stop ]]; then
    for svc in backend frontend; do
        pidfile="$NATIVE_DIR/$svc.pid"
        if [[ -f $pidfile ]]; then
            pid=$(cat "$pidfile")
            kill "$pid" 2>/dev/null && ok "$svc stopped (pid $pid)" || warn "$svc (pid $pid) was not running."
            rm -f "$pidfile"
        fi
    done
    exit 0
fi

BACKEND_URL=""

# ════════════════════════════════════════════════════════════════════════════
#  Phase 1 — deploy
# ════════════════════════════════════════════════════════════════════════════
info "flow-weaver — first-run setup"

if [[ $COMMAND == configure ]]; then
    default_port=$(env_get BACKEND_PORT); default_port=${default_port:-8080}
    BACKEND_URL=$(ask "Backend URL" "http://localhost:$default_port")
else
    say "How do you want to run flow-weaver?"
    say "  1) docker  — full stack via Docker Compose (recommended)"
    say "  2) native  — dotnet + node on this host, against an existing Postgres"
    say "  3) skip    — it is already running; just do the initial configuration"
    mode=$(ask "Choice" "1")

    case $mode in
        1|docker)
            [[ -x ./run.sh ]] || chmod +x ./run.sh || true
            ./run.sh up
            BACKEND_URL="http://localhost:$(env_get BACKEND_PORT)"
            ;;
        2|native)
            info "Native deployment (no Docker)"
            warn "Note: python_snippet steps need the bwrap sandbox (Linux). On other"
            warn "hosts they will not run — everything else works. Docker is the"
            warn "recommended mode for production."

            command -v dotnet >/dev/null 2>&1 || die "dotnet SDK not found on PATH."
            command -v node   >/dev/null 2>&1 || die "node not found on PATH."
            command -v npm    >/dev/null 2>&1 || die "npm not found on PATH."

            DB_HOST=$(ask "Postgres host" "$(env_get DB_HOST || true)")
            [[ -n $DB_HOST && $DB_HOST != db ]] || DB_HOST=localhost
            DB_PORT=$(ask "Postgres port" "${DB_PORT:-$(env_get DB_PORT || true)}")
            [[ -n $DB_PORT ]] || DB_PORT=5432
            DB_NAME=$(ask "Database name" "$(env_get POSTGRES_DB)")
            [[ -n $DB_NAME ]] || DB_NAME=flowweaver
            DB_USER=$(ask "Database user" "$(env_get POSTGRES_USER)")
            [[ -n $DB_USER ]] || DB_USER=flowweaver
            DB_PASSWORD=$(ask_secret "Database password")
            [[ -n $DB_PASSWORD ]] || die "Database password is required for native mode."

            BACKEND_PORT=$(ask "Backend port" "$(env_get BACKEND_PORT)")
            [[ -n $BACKEND_PORT ]] || BACKEND_PORT=8080
            FRONTEND_PORT=$(ask "Frontend port" "$(env_get FRONTEND_PORT)")
            [[ -n $FRONTEND_PORT ]] || FRONTEND_PORT=3000
            APP_ENV=$(ask "Environment (Development/Production)" "Development")

            JWT_KEY=$(env_get JWT_KEY)
            [[ -n $JWT_KEY ]] || { JWT_KEY=$(gen_key); say "Generated a JWT signing key."; }

            env_set DB_HOST "$DB_HOST";       env_set DB_PORT "$DB_PORT"
            env_set POSTGRES_DB "$DB_NAME";   env_set POSTGRES_USER "$DB_USER"
            env_set POSTGRES_PASSWORD "$DB_PASSWORD"
            env_set BACKEND_PORT "$BACKEND_PORT"; env_set FRONTEND_PORT "$FRONTEND_PORT"
            env_set JWT_KEY "$JWT_KEY"

            mkdir -p "$NATIVE_DIR/keyring"

            info "Building backend (dotnet publish)…"
            dotnet publish ../flow_weaver_backend/flow_weaver_backend.csproj \
                -c Release -o "$NATIVE_DIR/backend" --nologo

            info "Building frontend (npm)…"
            (cd ../frontend && npm ci --no-audit --no-fund && npm run build)

            info "Starting backend…"
            (
                export ASPNETCORE_ENVIRONMENT="$APP_ENV"
                export ASPNETCORE_URLS="http://localhost:$BACKEND_PORT"
                export ConnectionStrings__DefaultConnection="Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD"
                export Jwt__Key="$JWT_KEY"
                export DataProtection__KeyRingPath="$PWD/$NATIVE_DIR/keyring"
                nohup dotnet "$NATIVE_DIR/backend/flow_weaver_backend.dll" \
                    > "$NATIVE_DIR/backend.log" 2>&1 &
                echo $! > "$NATIVE_DIR/backend.pid"
            )
            ok "Backend started (log: deploy/$NATIVE_DIR/backend.log)"

            info "Starting frontend…"
            (
                export NODE_ENV=production HOST=0.0.0.0 PORT="$FRONTEND_PORT"
                export ORIGIN="http://localhost:$FRONTEND_PORT"
                export BACKEND_URL="http://localhost:$BACKEND_PORT"
                cd ../frontend
                nohup node build/index.js \
                    > "../deploy/$NATIVE_DIR/frontend.log" 2>&1 &
                echo $! > "../deploy/$NATIVE_DIR/frontend.pid"
            )
            ok "Frontend started (log: deploy/$NATIVE_DIR/frontend.log)"
            say "Stop both later with: ./setup.sh stop"

            BACKEND_URL="http://localhost:$BACKEND_PORT"
            ;;
        3|skip)
            default_port=$(env_get BACKEND_PORT); default_port=${default_port:-8080}
            BACKEND_URL=$(ask "Backend URL" "http://localhost:$default_port")
            [[ $BACKEND_URL == http* ]] || BACKEND_URL="http://localhost:8080"
            ;;
        *) die "Unknown choice: $mode" ;;
    esac
fi
BACKEND_URL=${BACKEND_URL%/}

# ════════════════════════════════════════════════════════════════════════════
#  Phase 2 — wait for the backend
# ════════════════════════════════════════════════════════════════════════════
info "Waiting for the backend at $BACKEND_URL …"
up=0
for _ in $(seq 1 60); do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$BACKEND_URL/api/auth/me" || true)
    if [[ $code != 000 ]]; then up=1; break; fi
    sleep 2
done
(( up )) || die "The backend did not answer after 2 minutes. Check the logs and re-run: ./setup.sh configure"
ok "Backend is answering."

# ════════════════════════════════════════════════════════════════════════════
#  Phase 3 — initial credentials
# ════════════════════════════════════════════════════════════════════════════
info "Initial admin account"
ADMIN_USER=$(ask "Admin username" "admin")
say "Leave the password empty to have a strong one generated (shown once)."
ADMIN_PASS=$(ask_secret "Admin password (min 8 chars, empty = generate)")

if [[ -n $ADMIN_PASS ]]; then
    body=$(printf '{"username":"%s","password":"%s"}' "$ADMIN_USER" "$ADMIN_PASS")
else
    body=$(printf '{"username":"%s"}' "$ADMIN_USER")
fi

resp=$(curl -s -w '\n%{http_code}' -X POST "$BACKEND_URL/api/auth/bootstrap" \
    -H 'Content-Type: application/json' -d "$body")
code=${resp##*$'\n'}
resp=${resp%$'\n'*}

TOKEN=""
if [[ $code == 200 ]]; then
    TOKEN=$(json_get "$resp" access_token)
    created_user=$(json_get "$resp" username)
    created_pass=$(json_get "$resp" initial_password)
    ok "Admin account created."
    say ""
    say "  ${BOLD}Username : $created_user${OFF}"
    say "  ${BOLD}Password : $created_pass${OFF}"
    say "  ${YELLOW}Save these now — the password is not retrievable later.${OFF}"
elif [[ $code == 404 ]]; then
    warn "This instance already has users — logging in instead."
    ADMIN_USER=$(ask "Existing admin username" "$ADMIN_USER")
    [[ -n $ADMIN_PASS ]] || ADMIN_PASS=$(ask_secret "Password")
    login=$(curl -s -X POST "$BACKEND_URL/api/auth/login" -H 'Content-Type: application/json' \
        -d "$(printf '{"username":"%s","password":"%s"}' "$ADMIN_USER" "$ADMIN_PASS")")
    TOKEN=$(json_get "$login" access_token)
    [[ -n $TOKEN ]] || die "Login failed: $login"
    ok "Logged in as $ADMIN_USER."
else
    die "Bootstrap failed (HTTP $code): $resp"
fi
AUTH=(-H "Authorization: Bearer $TOKEN")

# ════════════════════════════════════════════════════════════════════════════
#  Phase 4 — AI provider
# ════════════════════════════════════════════════════════════════════════════
info "AI provider"
existing=$(curl -s "${AUTH[@]}" "$BACKEND_URL/api/AIProvider")
if [[ $existing == *'"ai_provider_id"'* ]]; then
    warn "At least one AI provider already exists."
    skip=$(ask "Create another one anyway? (y/n)" "n")
    [[ $skip =~ ^[yYsS] ]] || existing_skip=1
fi

PROVIDER_ID=""
if [[ -z ${existing_skip:-} ]]; then
    say "  1) openai      (suggested model: gpt-5.5)"
    say "  2) anthropic   (suggested model: claude-sonnet-5)"
    say "  3) gemini      (suggested model: gemini-2.5-pro)"
    say "  4) ollama      (local, needs base URL; e.g. llama3.1)"
    say "  5) skip"
    ptype_choice=$(ask "Provider" "1")
    case $ptype_choice in
        1|openai)    PTYPE=openai;    PMODEL=gpt-5.5 ;;
        2|anthropic) PTYPE=anthropic; PMODEL=claude-sonnet-5 ;;
        3|gemini)    PTYPE=gemini;    PMODEL=gemini-2.5-pro ;;
        4|ollama)    PTYPE=ollama;    PMODEL=llama3.1 ;;
        *)           PTYPE="" ;;
    esac

    if [[ -n $PTYPE ]]; then
        PNAME=$(ask "Provider name" "$PTYPE")
        PMODEL=$(ask "Default model" "$PMODEL")
        PKEY=""; PBASE=""
        if [[ $PTYPE == ollama ]]; then
            PBASE=$(ask "Ollama base URL" "http://localhost:11434")
        else
            PKEY=$(ask_secret "API key")
            PBASE=$(ask "Base URL (empty = provider default)" "")
        fi

        pbody=$(printf '{"name":"%s","type":"%s","default_model":"%s"' "$PNAME" "$PTYPE" "$PMODEL")
        [[ -n $PKEY  ]] && pbody+=$(printf ',"api_key":"%s"' "$PKEY")
        [[ -n $PBASE ]] && pbody+=$(printf ',"base_url":"%s"' "$PBASE")
        pbody+='}'

        presp=$(curl -s -w '\n%{http_code}' -X POST "$BACKEND_URL/api/AIProvider" \
            "${AUTH[@]}" -H 'Content-Type: application/json' -d "$pbody")
        pcode=${presp##*$'\n'}; presp=${presp%$'\n'*}
        [[ $pcode == 200 || $pcode == 201 ]] || die "Provider create failed (HTTP $pcode): $presp"
        PROVIDER_ID=$(json_get "$presp" ai_provider_id)
        ok "Provider '$PNAME' created ($PROVIDER_ID)."

        say "Testing connectivity (one tiny prompt)…"
        tresp=$(curl -s -X POST "$BACKEND_URL/api/AIProvider/$PROVIDER_ID/test" "${AUTH[@]}")
        if [[ $tresp == *'"success":true'* ]]; then
            ok "Provider answered: $(json_get "$tresp" response)"
        else
            warn "Provider test FAILED: $(json_get "$tresp" response)"
            warn "The provider was saved — fix the key/model later in /ai."
        fi
    fi
fi

# ════════════════════════════════════════════════════════════════════════════
#  Phase 5 — assistant agent
# ════════════════════════════════════════════════════════════════════════════
info "AI agent"
agents=$(curl -s "${AUTH[@]}" "$BACKEND_URL/api/AIAgent")
if [[ $agents == *'"role":"assistant"'* ]]; then
    ok "An assistant agent already exists — the chat is ready."
else
    if [[ -z $PROVIDER_ID ]]; then
        # Fall back to the first provider on the instance.
        PROVIDER_ID=$(json_get "$(curl -s "${AUTH[@]}" "$BACKEND_URL/api/AIProvider")" ai_provider_id)
    fi
    if [[ -z $PROVIDER_ID ]]; then
        warn "No AI provider available — skipping agent creation. Re-run './setup.sh configure' after adding one."
    else
        abody=$(printf '{"name":"Flow Weaver Assistant","role":"assistant","description":"Default assistant created by setup","provider_id":"%s","system_prompt":"","max_iterations":20,"temperature":0.2}' "$PROVIDER_ID")
        aresp=$(curl -s -w '\n%{http_code}' -X POST "$BACKEND_URL/api/AIAgent" \
            "${AUTH[@]}" -H 'Content-Type: application/json' -d "$abody")
        acode=${aresp##*$'\n'}; aresp=${aresp%$'\n'*}
        if [[ $acode == 200 || $acode == 201 ]]; then
            ok "Assistant agent created (all tools enabled)."
        else
            warn "Agent create failed (HTTP $acode): $aresp"
        fi
    fi
fi

# ════════════════════════════════════════════════════════════════════════════
#  Done
# ════════════════════════════════════════════════════════════════════════════
FRONTEND_URL=$(env_get FRONTEND_ORIGIN)
[[ -n $FRONTEND_URL ]] || FRONTEND_URL="http://localhost:$(env_get FRONTEND_PORT)"
info "Setup complete"
say "  Frontend : ${FRONTEND_URL}"
say "  API      : $BACKEND_URL/api"
say "  Login    : $ADMIN_USER (password above)"
say ""
say "Next steps: add devices and credentials in the UI, then ask the"
say "assistant to build your first workflow."
