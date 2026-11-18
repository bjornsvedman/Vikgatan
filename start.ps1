param(
    [ValidateSet('Start', 'Api', 'Frontend')]
    [string]$Mode = 'Start'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$apiProject = Join-Path $root 'InvoiceManager.Api\InvoiceManager.Api.csproj'
$frontend = Join-Path $root 'frontend'

if ($Mode -eq 'Api') {
    Set-Location $root
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    & dotnet run --project $apiProject --no-launch-profile --urls http://localhost:5084
    exit $LASTEXITCODE
}

if ($Mode -eq 'Frontend') {
    Set-Location $root
    & npm.cmd --prefix $frontend run dev -- --host 127.0.0.1
    exit $LASTEXITCODE
}

foreach ($command in @('dotnet', 'npm.cmd', 'docker')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found. Install the prerequisites listed in README.md."
    }
}

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) {
    throw "Local .env is missing. Follow the one-time setup in README.md before starting the app."
}

$secrets = & dotnet user-secrets list --project $apiProject 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($secrets | Where-Object { $_ -match '^ConnectionStrings:Postgres\s*=' })) {
    throw "ConnectionStrings:Postgres is not set in .NET User Secrets. Follow the one-time setup in README.md."
}

function Test-DockerEngine {
    & docker info --format '{{.ServerVersion}}' 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}

if (-not (Test-DockerEngine)) {
    $dockerDesktop = @(
        (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'),
        (Join-Path $env:LocalAppData 'Programs\Docker\Docker\Docker Desktop.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $dockerDesktop) {
        throw 'Docker Desktop is not available. Install it and try again.'
    }

    Write-Host 'Starting Docker Desktop...'
    Start-Process -FilePath $dockerDesktop
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-DockerEngine) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
    }
    if (-not (Test-DockerEngine)) {
        throw 'Docker Desktop did not become ready within two minutes.'
    }
}

Set-Location $root
& docker compose up -d --wait
if ($LASTEXITCODE -ne 0) {
    throw 'PostgreSQL did not start successfully.'
}

if (-not (Test-Path (Join-Path $frontend 'node_modules'))) {
    Write-Host 'Installing frontend dependencies...'
    & npm.cmd --prefix $frontend ci
    if ($LASTEXITCODE -ne 0) {
        throw 'Frontend dependency installation failed.'
    }
}

function Test-LocalPort {
    param([int]$Port)

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

$apiAlreadyRunning = $false
if (Test-LocalPort 5084) {
    try {
        $apiResponse = Invoke-RestMethod -Uri 'http://localhost:5084/' -TimeoutSec 3
        $apiAlreadyRunning = $apiResponse.name -eq 'Invoice Manager API'
    }
    catch {}
    if (-not $apiAlreadyRunning) {
        throw 'Port 5084 is already in use by another application.'
    }
}

$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
try {
    Write-Host 'Applying database migrations...'
    if ($apiAlreadyRunning) {
        & dotnet ef database update --project $apiProject --no-build
    }
    else {
        & dotnet ef database update --project $apiProject
    }
    if ($LASTEXITCODE -ne 0) {
        throw 'Database migrations failed.'
    }
}
finally {
    if ($null -eq $previousEnvironment) {
        Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    }
    else {
        $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    }
}

function Start-AppWindow {
    param([ValidateSet('Api', 'Frontend')][string]$ChildMode)

    $quotedScript = '"{0}"' -f $PSCommandPath
    $arguments = "-NoProfile -ExecutionPolicy Bypass -NoExit -File $quotedScript -Mode $ChildMode"
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WorkingDirectory $root
}

function Wait-ForApi {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        try {
            $response = Invoke-RestMethod -Uri 'http://localhost:5084/' -TimeoutSec 3
            if ($response.name -eq 'Invoice Manager API' -and $response.status -eq 'running') {
                return
            }
        }
        catch {}
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw 'The API did not become ready at http://localhost:5084/. Check its console window.'
}

function Wait-ForFrontend {
    $deadline = (Get-Date).AddSeconds(60)
    do {
        try {
            $response = Invoke-WebRequest -Uri 'http://127.0.0.1:5173/' -TimeoutSec 3 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {}
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw 'The frontend did not become ready at http://127.0.0.1:5173/. Check its console window.'
}

if (-not $apiAlreadyRunning) {
    Start-AppWindow -ChildMode Api
}
Wait-ForApi

if (-not (Test-LocalPort 5173)) {
    Start-AppWindow -ChildMode Frontend
}
Wait-ForFrontend

$proxyResponse = Invoke-WebRequest -Uri 'http://127.0.0.1:5173/api/properties' -TimeoutSec 10 -UseBasicParsing
if ($proxyResponse.StatusCode -ne 200) {
    throw 'The frontend is running, but its API proxy did not return HTTP 200.'
}

Start-Process 'http://127.0.0.1:5173/'
Write-Host 'Invoice Manager is running at http://127.0.0.1:5173/'
