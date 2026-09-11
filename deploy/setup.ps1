# flow-weaver first-run wizard (Windows). Linux/macOS: setup.sh — same flow.
#
# One command from zero to a working instance:
#   1. Deploy      — with Docker (delegates to run.ps1, which configures
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
#   .\setup.ps1              # full wizard
#   .\setup.ps1 configure    # skip deploy, only bootstrap an existing backend
#   .\setup.ps1 stop         # stop a native (non-Docker) deployment
#
# Safe to re-run: every step detects existing state and offers to skip.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('', 'configure', 'stop')]
    [string]$Command = ''
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$EnvFile = Join-Path $PSScriptRoot '.env'
$NativeDir = Join-Path $PSScriptRoot 'native'

function Say([string]$m)  { Write-Host $m }
function Info([string]$m) { Write-Host "`n$m" -ForegroundColor White -BackgroundColor DarkBlue }
function Ok([string]$m)   { Write-Host "✓ $m" -ForegroundColor Green }
function Warn([string]$m) { Write-Host $m -ForegroundColor Yellow }
function Die([string]$m)  { Write-Host $m -ForegroundColor Red; exit 1 }

function Ask([string]$Prompt, [string]$Default = '') {
    $suffix = if ($Default) { " [$Default]" } else { '' }
    $answer = Read-Host "$Prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { $Default } else { $answer }
}

function AskSecret([string]$Prompt) {
    $secure = Read-Host -AsSecureString $Prompt
    [System.Net.NetworkCredential]::new('', $secure).Password
}

# Minimal .env helpers (same $$-escaping contract as run.ps1 / compose).
function EnvGet([string]$Key) {
    if (-not (Test-Path $EnvFile)) { return '' }
    $line = Select-String -Path $EnvFile -Pattern "^$([regex]::Escape($Key))=" |
        Select-Object -Last 1
    if (-not $line) { return '' }
    ($line.Line.Substring($Key.Length + 1)) -replace '\$\$', '$'
}
function EnvSet([string]$Key, [string]$Value) {
    $escaped = $Value -replace '\$', '$$$$'
    if (-not (Test-Path $EnvFile)) { New-Item -ItemType File $EnvFile | Out-Null }
    $lines = @(Get-Content $EnvFile)
    $found = $false
    $lines = $lines | ForEach-Object {
        if (-not $found -and $_ -match "^$([regex]::Escape($Key))=") { $found = $true; "$Key=$escaped" }
        else { $_ }
    }
    if (-not $found) { $lines += "$Key=$escaped" }
    Set-Content -Path $EnvFile -Value $lines
}

function GenKey {
    $bytes = [byte[]]::new(48)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes)
}

# ── stop (native mode) ──────────────────────────────────────────────────────
if ($Command -eq 'stop') {
    foreach ($svc in 'backend', 'frontend') {
        $pidFile = Join-Path $NativeDir "$svc.pid"
        if (Test-Path $pidFile) {
            $procId = Get-Content $pidFile
            try { Stop-Process -Id $procId -Force -ErrorAction Stop; Ok "$svc stopped (pid $procId)" }
            catch { Warn "$svc (pid $procId) was not running." }
            Remove-Item $pidFile -Force
        }
    }
    exit 0
}

$BackendUrl = ''

# ════════════════════════════════════════════════════════════════════════════
#  Phase 1 — deploy
# ════════════════════════════════════════════════════════════════════════════
Info 'flow-weaver — first-run setup'

if ($Command -eq 'configure') {
    $defaultPort = EnvGet 'BACKEND_PORT'; if (-not $defaultPort) { $defaultPort = '8080' }
    $BackendUrl = Ask 'Backend URL' "http://localhost:$defaultPort"
}
else {
    Say 'How do you want to run flow-weaver?'
    Say '  1) docker  — full stack via Docker Compose (recommended)'
    Say '  2) native  — dotnet + node on this host, against an existing Postgres'
    Say '  3) skip    — it is already running; just do the initial configuration'
    $mode = Ask 'Choice' '1'

    switch -Regex ($mode) {
        '^(1|docker)$' {
            & (Join-Path $PSScriptRoot 'run.ps1') up
            if ($LASTEXITCODE -ne 0) { Die 'Docker deployment failed — fix the error above and re-run.' }
            $BackendUrl = "http://localhost:$(EnvGet 'BACKEND_PORT')"
        }
        '^(2|native)$' {
            Info 'Native deployment (no Docker)'
            Warn 'Note: python_snippet steps need the bwrap sandbox (Linux). On Windows'
            Warn 'they will not run — everything else works. Docker is the recommended'
            Warn 'mode for production.'

            if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Die 'dotnet SDK not found on PATH.' }
            if (-not (Get-Command node   -ErrorAction SilentlyContinue)) { Die 'node not found on PATH.' }
            if (-not (Get-Command npm    -ErrorAction SilentlyContinue)) { Die 'npm not found on PATH.' }

            $dbHost = Ask 'Postgres host' ((EnvGet 'DB_HOST'), 'localhost' | Where-Object { $_ -and $_ -ne 'db' } | Select-Object -First 1)
            $dbPort = Ask 'Postgres port' ((EnvGet 'DB_PORT'), '5432' | Where-Object { $_ } | Select-Object -First 1)
            $dbName = Ask 'Database name' ((EnvGet 'POSTGRES_DB'), 'flowweaver' | Where-Object { $_ } | Select-Object -First 1)
            $dbUser = Ask 'Database user' ((EnvGet 'POSTGRES_USER'), 'flowweaver' | Where-Object { $_ } | Select-Object -First 1)
            $dbPass = AskSecret 'Database password'
            if (-not $dbPass) { Die 'Database password is required for native mode.' }

            $backendPort  = Ask 'Backend port'  ((EnvGet 'BACKEND_PORT'), '8080' | Where-Object { $_ } | Select-Object -First 1)
            $frontendPort = Ask 'Frontend port' ((EnvGet 'FRONTEND_PORT'), '3000' | Where-Object { $_ } | Select-Object -First 1)
            $appEnv = Ask 'Environment (Development/Production)' 'Development'

            $jwtKey = EnvGet 'JWT_KEY'
            if (-not $jwtKey) { $jwtKey = GenKey; Say 'Generated a JWT signing key.' }

            EnvSet 'DB_HOST' $dbHost;       EnvSet 'DB_PORT' $dbPort
            EnvSet 'POSTGRES_DB' $dbName;   EnvSet 'POSTGRES_USER' $dbUser
            EnvSet 'POSTGRES_PASSWORD' $dbPass
            EnvSet 'BACKEND_PORT' $backendPort; EnvSet 'FRONTEND_PORT' $frontendPort
            EnvSet 'JWT_KEY' $jwtKey

            New-Item -ItemType Directory -Force (Join-Path $NativeDir 'keyring') | Out-Null

            Info 'Building backend (dotnet publish)…'
            dotnet publish ..\flow_weaver_backend\flow_weaver_backend.csproj `
                -c Release -o (Join-Path $NativeDir 'backend') --nologo
            if ($LASTEXITCODE -ne 0) { Die 'Backend build failed.' }

            Info 'Building frontend (npm)…'
            Push-Location ..\frontend
            npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { Pop-Location; Die 'npm ci failed.' }
            npm run build;               if ($LASTEXITCODE -ne 0) { Pop-Location; Die 'Frontend build failed.' }
            Pop-Location

            Info 'Starting backend…'
            $env:ASPNETCORE_ENVIRONMENT = $appEnv
            $env:ASPNETCORE_URLS = "http://localhost:$backendPort"
            $env:ConnectionStrings__DefaultConnection =
                "Host=$dbHost;Port=$dbPort;Database=$dbName;Username=$dbUser;Password=$dbPass"
            $env:Jwt__Key = $jwtKey
            $env:DataProtection__KeyRingPath = (Join-Path $NativeDir 'keyring')
            $backendProc = Start-Process dotnet `
                -ArgumentList (Join-Path $NativeDir 'backend\flow_weaver_backend.dll') `
                -RedirectStandardOutput (Join-Path $NativeDir 'backend.log') `
                -RedirectStandardError  (Join-Path $NativeDir 'backend.err.log') `
                -PassThru -WindowStyle Hidden
            Set-Content (Join-Path $NativeDir 'backend.pid') $backendProc.Id
            Ok "Backend started (log: deploy\native\backend.log)"

            Info 'Starting frontend…'
            $env:NODE_ENV = 'production'; $env:HOST = '0.0.0.0'; $env:PORT = $frontendPort
            $env:ORIGIN = "http://localhost:$frontendPort"
            $env:BACKEND_URL = "http://localhost:$backendPort"
            $frontendProc = Start-Process node `
                -ArgumentList 'build\index.js' -WorkingDirectory (Resolve-Path ..\frontend) `
                -RedirectStandardOutput (Join-Path $NativeDir 'frontend.log') `
                -RedirectStandardError  (Join-Path $NativeDir 'frontend.err.log') `
                -PassThru -WindowStyle Hidden
            Set-Content (Join-Path $NativeDir 'frontend.pid') $frontendProc.Id
            Ok "Frontend started (log: deploy\native\frontend.log)"
            Say 'Stop both later with: .\setup.ps1 stop'

            $BackendUrl = "http://localhost:$backendPort"
        }
        '^(3|skip)$' {
            $defaultPort = EnvGet 'BACKEND_PORT'; if (-not $defaultPort) { $defaultPort = '8080' }
            $BackendUrl = Ask 'Backend URL' "http://localhost:$defaultPort"
        }
        default { Die "Unknown choice: $mode" }
    }
}
$BackendUrl = $BackendUrl.TrimEnd('/')

# ════════════════════════════════════════════════════════════════════════════
#  Phase 2 — wait for the backend
# ════════════════════════════════════════════════════════════════════════════
Info "Waiting for the backend at $BackendUrl …"
$up = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        Invoke-WebRequest -Uri "$BackendUrl/api/auth/me" -Method Get -TimeoutSec 3 | Out-Null
        $up = $true; break
    }
    catch {
        # Any HTTP status (401 included) means the server answered.
        if ($_.Exception.Response) { $up = $true; break }
        Start-Sleep -Seconds 2
    }
}
if (-not $up) { Die 'The backend did not answer after 2 minutes. Check the logs and re-run: .\setup.ps1 configure' }
Ok 'Backend is answering.'

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body = $null, [string]$Token = '')
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $params = @{ Method = $Method; Uri = "$BackendUrl$Path"; Headers = $headers; TimeoutSec = 30 }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 6)
        $params.ContentType = 'application/json'
    }
    Invoke-RestMethod @params
}

# ════════════════════════════════════════════════════════════════════════════
#  Phase 3 — initial credentials
# ════════════════════════════════════════════════════════════════════════════
Info 'Initial admin account'
$adminUser = Ask 'Admin username' 'admin'
Say 'Leave the password empty to have a strong one generated (shown once).'
$adminPass = AskSecret 'Admin password (min 8 chars, empty = generate)'

$bootstrapBody = @{ username = $adminUser }
if ($adminPass) { $bootstrapBody.password = $adminPass }

$token = ''
try {
    $resp = Invoke-Api POST '/api/auth/bootstrap' $bootstrapBody
    $token = $resp.access_token
    Ok 'Admin account created.'
    Say ''
    Say "  Username : $($resp.username)"
    Say "  Password : $($resp.initial_password)"
    Warn '  Save these now — the password is not retrievable later.'
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -eq 404) {
        Warn 'This instance already has users — logging in instead.'
        $adminUser = Ask 'Existing admin username' $adminUser
        if (-not $adminPass) { $adminPass = AskSecret 'Password' }
        $login = Invoke-Api POST '/api/auth/login' @{ username = $adminUser; password = $adminPass }
        $token = $login.access_token
        Ok "Logged in as $adminUser."
    }
    else { Die "Bootstrap failed: $($_.ErrorDetails.Message ?? $_.Exception.Message)" }
}
if (-not $token) { Die 'Could not obtain an access token.' }

# ════════════════════════════════════════════════════════════════════════════
#  Phase 4 — AI provider
# ════════════════════════════════════════════════════════════════════════════
Info 'AI provider'
$providerId = ''
$existing = Invoke-Api GET '/api/AIProvider' -Token $token
$skipProvider = $false
if ($existing.data -and $existing.data.Count -gt 0) {
    Warn 'At least one AI provider already exists.'
    $again = Ask 'Create another one anyway? (y/n)' 'n'
    if ($again -notmatch '^[yYsS]') { $skipProvider = $true; $providerId = $existing.data[0].ai_provider_id }
}

if (-not $skipProvider) {
    Say '  1) openai      (suggested model: gpt-5.5)'
    Say '  2) anthropic   (suggested model: claude-sonnet-5)'
    Say '  3) gemini      (suggested model: gemini-2.5-pro)'
    Say '  4) ollama      (local, needs base URL; e.g. llama3.1)'
    Say '  5) skip'
    $choice = Ask 'Provider' '1'
    $ptype = ''; $pmodel = ''
    switch -Regex ($choice) {
        '^(1|openai)$'    { $ptype = 'openai';    $pmodel = 'gpt-5.5' }
        '^(2|anthropic)$' { $ptype = 'anthropic'; $pmodel = 'claude-sonnet-5' }
        '^(3|gemini)$'    { $ptype = 'gemini';    $pmodel = 'gemini-2.5-pro' }
        '^(4|ollama)$'    { $ptype = 'ollama';    $pmodel = 'llama3.1' }
    }

    if ($ptype) {
        $pname  = Ask 'Provider name' $ptype
        $pmodel = Ask 'Default model' $pmodel
        $body = @{ name = $pname; type = $ptype; default_model = $pmodel }
        if ($ptype -eq 'ollama') {
            $body.base_url = Ask 'Ollama base URL' 'http://localhost:11434'
        }
        else {
            $key = AskSecret 'API key'
            if ($key) { $body.api_key = $key }
            $base = Ask 'Base URL (empty = provider default)' ''
            if ($base) { $body.base_url = $base }
        }

        $provider = Invoke-Api POST '/api/AIProvider' $body -Token $token
        $providerId = $provider.ai_provider_id
        Ok "Provider '$pname' created ($providerId)."

        Say 'Testing connectivity (one tiny prompt)…'
        $test = Invoke-Api POST "/api/AIProvider/$providerId/test" @{} -Token $token
        if ($test.success) { Ok "Provider answered: $($test.response)" }
        else {
            Warn "Provider test FAILED: $($test.response)"
            Warn 'The provider was saved — fix the key/model later in /ai.'
        }
    }
}

# ════════════════════════════════════════════════════════════════════════════
#  Phase 5 — assistant agent
# ════════════════════════════════════════════════════════════════════════════
Info 'AI agent'
$agents = Invoke-Api GET '/api/AIAgent' -Token $token
$hasAssistant = $agents.data | Where-Object { $_.role -eq 'assistant' } | Select-Object -First 1
if ($hasAssistant) {
    Ok 'An assistant agent already exists — the chat is ready.'
}
elseif (-not $providerId) {
    Warn "No AI provider available — skipping agent creation. Re-run '.\setup.ps1 configure' after adding one."
}
else {
    $agentBody = @{
        name           = 'Flow Weaver Assistant'
        role           = 'assistant'
        description    = 'Default assistant created by setup'
        provider_id    = $providerId
        system_prompt  = ''
        max_iterations = 20
        temperature    = 0.2
    }
    Invoke-Api POST '/api/AIAgent' $agentBody -Token $token | Out-Null
    Ok 'Assistant agent created (all tools enabled).'
}

# ════════════════════════════════════════════════════════════════════════════
#  Done
# ════════════════════════════════════════════════════════════════════════════
$frontendUrl = EnvGet 'FRONTEND_ORIGIN'
if (-not $frontendUrl) { $frontendUrl = "http://localhost:$(EnvGet 'FRONTEND_PORT')" }
Info 'Setup complete'
Say "  Frontend : $frontendUrl"
Say "  API      : $BackendUrl/api"
Say "  Login    : $adminUser (password above)"
Say ''
Say 'Next steps: add devices and credentials in the UI, then ask the'
Say 'assistant to build your first workflow.'
