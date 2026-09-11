#!/usr/bin/env pwsh
<#
.SYNOPSIS
flow-weaver launcher (Windows). Linux/macOS: use run.sh — same options.

.DESCRIPTION
Brings up backend + worker + frontend, with the database either managed by this
compose project or supplied externally. Every port and setting can be chosen on
the command line; anything you leave out is asked for (or taken from
deploy\.env, or defaulted) — pass -Yes to never prompt.

Settings are persisted to deploy\.env, so the next run reuses them.

.EXAMPLE
.\run.ps1
Interactive setup.

.EXAMPLE
.\run.ps1 -Yes
Defaults, no questions.

.EXAMPLE
.\run.ps1 -Yes -DbMode external -DbHost host.docker.internal -DbPassword s3cr3t

.EXAMPLE
.\run.ps1 -Yes -BackendPort 8090 -FrontendPort 3001

.EXAMPLE
.\run.ps1 logs backend
#>
[CmdletBinding()]
param(
    # up (default) | down | restart | logs | ps | config
    [Parameter(Position = 0)]
    [ValidateSet('up', 'down', 'restart', 'logs', 'ps', 'config')]
    [string]$Command = 'up',

    # Service name for `logs`.
    [Parameter(Position = 1)]
    [string]$Service,

    [Alias('y')]
    [switch]$Yes,

    [ValidateSet('docker', 'external')]
    [string]$DbMode,

    [string]$DbHost,
    [int]$DbPort,
    [string]$DbName,
    [string]$DbUser,
    [string]$DbPassword,

    [int]$BackendPort,
    [int]$FrontendPort,
    [int]$PostgresPort,

    [string]$AppEnv,
    [string]$Subnet,
    [string[]]$Services,

    [switch]$Tunnel,
    [switch]$DevOverrides,
    [switch]$NoDevOverrides,
    [switch]$Build,
    [switch]$NoBuild,
    [switch]$Pull,

    # Resolve everything and print the command without running it.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot
$EnvFile = '.env'

function Write-Info { param($m) Write-Host $m -ForegroundColor Cyan }
function Write-Warn { param($m) Write-Host $m -ForegroundColor Yellow }
function Write-Ok   { param($m) Write-Host $m -ForegroundColor Green }
function Die        { param($m) Write-Host $m -ForegroundColor Red; exit 1 }

# ── .env helpers ────────────────────────────────────────────────────────────
function Get-EnvValue {
    param([string]$Key)
    if (-not (Test-Path $EnvFile)) { return $null }
    $line = Select-String -Path $EnvFile -Pattern "^$([regex]::Escape($Key))=" |
            Select-Object -Last 1
    if (-not $line) { return $null }
    $raw = $line.Line -replace "^$([regex]::Escape($Key))=", ''
    # Stored form escapes $ as $$ (see Set-EnvValue); undo it so callers always
    # work with the logical value. Without this, re-reading and re-writing would
    # double the escaping on every run and silently corrupt passwords.
    return ($raw -replace '\$\$', '$')
}

function Set-EnvValue {
    param([string]$Key, [string]$Value)
    # Compose interpolates $ in .env values; $$ is the literal escape.
    $Value = $Value -replace '\$', '$$$$'
    if (-not (Test-Path $EnvFile)) { New-Item -ItemType File -Path $EnvFile | Out-Null }
    $lines = @(Get-Content $EnvFile)
    $pattern = "^$([regex]::Escape($Key))="
    if ($lines -match $pattern) {
        $done = $false
        $lines = $lines | ForEach-Object {
            if ($_ -match $pattern -and -not $done) { $done = $true; "$Key=$Value" } else { $_ }
        }
    } else {
        $lines += "$Key=$Value"
    }
    Set-Content -Path $EnvFile -Value $lines -Encoding utf8
}

# flag value, else .env, else default
function Resolve-Setting {
    param($Current, [string]$Key, $Default)
    if ($null -ne $Current -and "$Current" -ne '' -and "$Current" -ne '0') { return "$Current" }
    $fromEnv = Get-EnvValue $Key
    if ($fromEnv) { return $fromEnv }
    return "$Default"
}

function Ask {
    param([string]$Prompt, [string]$Default)
    if ($Yes) { return $Default }
    $answer = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer
}

function Ask-Secret {
    param([string]$Prompt, [string]$Default)
    if ($Yes) { return $Default }
    $suffix = if ($Default) { ' [sin cambios]' } else { '' }
    $secure = Read-Host "$Prompt$suffix" -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if ([string]::IsNullOrWhiteSpace($plain)) { return $Default }
    return $plain
}

# ── environment probing ─────────────────────────────────────────────────────
function Assert-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Die 'docker no está en el PATH.'
    }
    docker compose version *> $null
    if ($LASTEXITCODE -ne 0) { Die "'docker compose' (v2) no disponible. Instala el plugin Compose." }
    # A dry run only resolves configuration, so it works without a live daemon.
    if ($DryRun) { return }
    docker info *> $null
    if ($LASTEXITCODE -ne 0) { Die 'No se puede contactar con el demonio Docker. ¿Docker Desktop está arrancado?' }
}

function Get-UsedSubnets {
    $ids = docker network ls -q 2>$null
    if (-not $ids) { return @() }
    $out = foreach ($id in $ids) {
        docker network inspect $id --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}' 2>$null
    }
    return ($out -join ' ') -split '\s+' | Where-Object { $_ }
}

# Picks a /16 nobody is using. Sticks to 172.16-172.31 (Docker's own default
# pool) rather than 10.x or 192.168.x, which on a network-automation host are
# usually the device management ranges — a Docker subnet overlapping those
# would blackhole traffic to the very equipment this platform drives.
function Get-FreeSubnet {
    $used = Get-UsedSubnets
    foreach ($i in 16..31) {
        if (-not ($used | Where-Object { $_ -like "172.$i.*" })) { return "172.$i.0.0/16" }
    }
    Write-Warn 'No queda ningún /16 libre en 172.16-172.31; se usará 10.199.0.0/16.'
    return '10.199.0.0/16'
}

function Test-SubnetFree {
    param([string]$Cidr)
    $prefix = ($Cidr -split '\.')[0..1] -join '.'
    $used = Get-UsedSubnets
    return -not ($used | Where-Object { $_ -like "$prefix.*" })
}

function Test-PortBusy {
    param([int]$Port)
    try {
        return [bool](Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    } catch { return $false }
}

function Test-TcpReachable {
    param([string]$TargetHost, [int]$Port)
    try {
        $client = [Net.Sockets.TcpClient]::new()
        $ok = $client.ConnectAsync($TargetHost, $Port).Wait(3000)
        $client.Close()
        return $ok
    } catch { return $false }
}

function New-JwtKey {
    $bytes = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes)
}

# ── compose file set ────────────────────────────────────────────────────────
function Get-ComposeFiles {
    $files = @('-f', 'docker-compose.yml')
    if ((Get-EnvValue 'DB_MODE') -eq 'external') { $files += @('-f', 'compose.external-db.yml') }

    $includeDev = $false
    if ($DevOverrides) {
        $includeDev = $true
    } elseif (-not $NoDevOverrides) {
        docker network inspect shared-net *> $null
        $includeDev = ($LASTEXITCODE -eq 0)
    }
    if ($includeDev) { $files += @('-f', 'docker-compose.override.yml') }
    return $files
}

if (-not $Yes -and -not [Environment]::UserInteractive) {
    $Yes = $true
    Write-Warn 'Sin consola interactiva: se asume -Yes (defaults + flags).'
}

Assert-Docker
$files = Get-ComposeFiles

# ── simple commands need no configuration ───────────────────────────────────
switch ($Command) {
    'down' {
        Write-Info 'Parando flow-weaver…'
        & docker compose @files down --remove-orphans
        exit $LASTEXITCODE
    }
    'logs' {
        $logArgs = @('logs', '-f', '--tail=200')
        if ($Service) { $logArgs += $Service }
        & docker compose @files @logArgs
        exit $LASTEXITCODE
    }
    'ps'     { & docker compose @files ps;     exit $LASTEXITCODE }
    'config' { & docker compose @files config; exit $LASTEXITCODE }
}

# ── gather configuration ────────────────────────────────────────────────────
Write-Info 'flow-weaver — configuración'
Write-Host 'Los valores entre corchetes son los actuales (deploy\.env) o el default.'
Write-Host ''

$dbMode = Resolve-Setting $DbMode 'DB_MODE' 'docker'
if (-not $Yes) { $dbMode = Ask "Base de datos: 'docker' (gestionada aquí) o 'external'" $dbMode }
if ($dbMode -notin @('docker', 'external')) { Die "-DbMode debe ser 'docker' o 'external'." }

$dbName     = Resolve-Setting $DbName     'POSTGRES_DB'       'flowweaver'
$dbUser     = Resolve-Setting $DbUser     'POSTGRES_USER'     'flowweaver'
$dbPassword = Resolve-Setting $DbPassword 'POSTGRES_PASSWORD' ''

if ($dbMode -eq 'docker') {
    $pgPort = Resolve-Setting $PostgresPort 'POSTGRES_PORT' '5432'
    if (-not $Yes) {
        $dbName     = Ask        '  Nombre de la base de datos' $dbName
        $dbUser     = Ask        '  Usuario'                    $dbUser
        $dbPassword = Ask-Secret '  Contraseña'                 $dbPassword
        $pgPort     = Ask        '  Puerto publicado de Postgres' $pgPort
    }
    if (-not $dbPassword) { $dbPassword = 'flowweaver' }
    $dbHostValue = 'db'; $dbPortValue = '5432'
    if (Test-PortBusy ([int]$pgPort)) { Write-Warn "Aviso: el puerto $pgPort ya está escuchando en el host." }
} else {
    $dbHostValue = Resolve-Setting $DbHost 'DB_HOST' 'host.docker.internal'
    $dbPortValue = Resolve-Setting $DbPort 'DB_PORT' '5432'
    if (-not $Yes) {
        $dbHostValue = Ask        '  Host de la base de datos'  $dbHostValue
        $dbPortValue = Ask        '  Puerto'                    $dbPortValue
        $dbName      = Ask        '  Nombre de la base de datos' $dbName
        $dbUser      = Ask        '  Usuario'                   $dbUser
        $dbPassword  = Ask-Secret '  Contraseña'                $dbPassword
    }
    if (-not $dbPassword) { Die 'La contraseña de la base de datos externa es obligatoria (-DbPassword).' }

    # From inside a container, localhost is the container itself.
    if ($dbHostValue -in @('localhost', '127.0.0.1', '::1')) {
        Write-Warn "'$dbHostValue' apunta al propio contenedor; se usará host.docker.internal."
        $dbHostValue = 'host.docker.internal'
    }

    $probe = if ($dbHostValue -eq 'host.docker.internal') { '127.0.0.1' } else { $dbHostValue }
    if (Test-TcpReachable $probe ([int]$dbPortValue)) {
        Write-Ok "  OK: $probe`:$dbPortValue responde."
    } else {
        Write-Warn "  No se pudo conectar a $probe`:$dbPortValue desde el host."
        Write-Warn '  Si la base sólo escucha dentro de otra red Docker, se seguirá intentando al arrancar.'
    }
}

$backendPort  = Resolve-Setting $BackendPort  'BACKEND_PORT'  '8080'
$frontendPort = Resolve-Setting $FrontendPort 'FRONTEND_PORT' '3000'
$appEnv       = Resolve-Setting $AppEnv       'ASPNETCORE_ENVIRONMENT' 'Production'
if (-not $Yes) {
    Write-Host ''
    $backendPort  = Ask 'Puerto del backend'             $backendPort
    $frontendPort = Ask 'Puerto del frontend'            $frontendPort
    $appEnv       = Ask 'Entorno (Production/Development)' $appEnv
}
if (Test-PortBusy ([int]$backendPort))  { Write-Warn "Aviso: el puerto $backendPort ya está escuchando en el host." }
if (Test-PortBusy ([int]$frontendPort)) { Write-Warn "Aviso: el puerto $frontendPort ya está escuchando en el host." }

# The frontend's ORIGIN must match the URL the browser actually uses, or
# SvelteKit rejects form posts as cross-origin.
$origin = Get-EnvValue 'FRONTEND_ORIGIN'
if (-not $origin -or $origin -like 'http://localhost:*') { $origin = "http://localhost:$frontendPort" }
if (-not $Yes) { $origin = Ask 'URL pública del frontend (ORIGIN)' $origin }

# Subnet: reuse what is stored if it is still free, otherwise find a gap.
$subnetValue = Resolve-Setting $Subnet 'COMPOSE_SUBNET' ''
if (-not $subnetValue) {
    $subnetValue = Get-FreeSubnet
    Write-Host "Subred libre elegida automáticamente: $subnetValue"
} elseif (-not (Test-SubnetFree $subnetValue)) {
    docker network inspect flow-weaver_flow-weaver-net *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "La subred $subnetValue está ocupada por otra red Docker."
        $subnetValue = Get-FreeSubnet
        Write-Warn "Se usará $subnetValue en su lugar."
    }
}

$jwtKey = Get-EnvValue 'JWT_KEY'
if (-not $jwtKey) {
    $jwtKey = New-JwtKey
    Write-Host 'JWT_KEY generada (48 bytes aleatorios) y guardada en deploy\.env.'
}

# ── persist ─────────────────────────────────────────────────────────────────
Set-EnvValue 'DB_MODE'                $dbMode
Set-EnvValue 'DB_HOST'                $dbHostValue
Set-EnvValue 'DB_PORT'                $dbPortValue
Set-EnvValue 'POSTGRES_DB'            $dbName
Set-EnvValue 'POSTGRES_USER'          $dbUser
Set-EnvValue 'POSTGRES_PASSWORD'      $dbPassword
Set-EnvValue 'BACKEND_PORT'           $backendPort
Set-EnvValue 'FRONTEND_PORT'          $frontendPort
Set-EnvValue 'FRONTEND_ORIGIN'        $origin
Set-EnvValue 'ASPNETCORE_ENVIRONMENT' $appEnv
Set-EnvValue 'COMPOSE_SUBNET'         $subnetValue
Set-EnvValue 'TRUSTED_PROXIES'        $subnetValue   # must track the subnet, see docker-compose.yml
Set-EnvValue 'JWT_KEY'                $jwtKey
if ($dbMode -eq 'docker') { Set-EnvValue 'POSTGRES_PORT' $pgPort }

# ── build the command ───────────────────────────────────────────────────────
$files = Get-ComposeFiles
if ($Tunnel) { $files += @('--profile', 'tunnel') }

$serviceList = @()
if ($Services) { $serviceList = $Services }
elseif ($dbMode -eq 'external') { $serviceList = @('backend', 'worker', 'frontend') }

$upArgs = @('up', '-d', '--remove-orphans')
if ($Build) {
    $upArgs += '--build'
} elseif (-not $NoBuild) {
    docker image inspect flow-weaver-backend:latest *> $null
    if ($LASTEXITCODE -ne 0) { $upArgs += '--build' }
}

Write-Host ''
Write-Info 'Resumen'
Write-Host "  Base de datos   : $dbMode ($dbHostValue`:$dbPortValue/$dbName como $dbUser)"
Write-Host "  Backend         : http://localhost:$backendPort"
Write-Host "  Frontend        : $origin (puerto $frontendPort)"
Write-Host "  Entorno         : $appEnv"
Write-Host "  Subred          : $subnetValue (TRUSTED_PROXIES en sincronía)"
Write-Host "  Servicios       : $(if ($serviceList) { $serviceList -join ' ' } else { 'todos' })"
Write-Host "  Compose         : $($files -join ' ')"
Write-Host ''

if ($DryRun) {
    Write-Warn '-DryRun: no se ejecuta nada. El comando sería:'
    Write-Host "  docker compose $($files -join ' ') $($upArgs -join ' ') $($serviceList -join ' ')"
    exit 0
}

if (-not $Yes) {
    $confirm = Ask '¿Arrancar ahora? (s/n)' 's'
    if ($confirm -notmatch '^[sSyY]') {
        Write-Host 'Cancelado. La configuración quedó guardada en deploy\.env.'
        exit 0
    }
}

if ($Pull) { & docker compose @files pull --ignore-buildable }

if ($Command -eq 'restart') { & docker compose @files down --remove-orphans }

& docker compose @files @upArgs @serviceList
if ($LASTEXITCODE -ne 0) { Die "docker compose falló con código $LASTEXITCODE." }

Write-Host ''
Write-Ok 'Listo.'
Write-Host "  Frontend : $origin"
Write-Host "  API      : http://localhost:$backendPort/api/v1"
Write-Host '  Logs     : .\run.ps1 logs backend'
Write-Host ''
Write-Host 'Las migraciones se aplican solas al arrancar el backend; compruébalo con:'
Write-Host "  .\run.ps1 logs backend"
