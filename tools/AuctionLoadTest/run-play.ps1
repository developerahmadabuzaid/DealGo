<#
.SYNOPSIS
    A local copy of DealGo with a demo auction you bid in alongside simulated bidders.

.DESCRIPTION
    Starts a throwaway PostgreSQL in Docker, publishes the API and runs it on this machine (as
    SmarterASP does: Redis off; not Production, so sign-in codes are shown in the app instead of
    being texted). Then `AuctionLoadTest play` creates a demo auction with -Bots bidders in it.

    Open http://127.0.0.1:<port>/app/ on this computer, or http://<this computer's address>:<port>/app/
    on a phone on the same Wi-Fi, sign in with any phone number (the code fills itself in), and join
    the auction. It starts -Countdown seconds later and the bots bid until it ends.

    Nothing here touches dealgosa.com. Ctrl+C stops everything and removes the database.

.EXAMPLE
    .\tools\AuctionLoadTest\run-play.ps1
    .\tools\AuctionLoadTest\run-play.ps1 -Bots 40 -Rounds 0      # a new auction after each one, until Ctrl+C
#>
[CmdletBinding()]
param(
    [int]$Bots = 20,
    [int]$Rounds = 1,
    [int]$DurationSeconds = 180,
    [int]$Countdown = 20,
    [double]$Rate = 0.05,
    [double]$BurstRate = 0.25,
    [double]$Increment = 1,
    [int]$ApiPort = 5199,
    [int]$PostgresPort = 5463,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $root 'artifacts\play'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$container = "dealgo-play-pg"
$database = "auctions_play"
$dbPassword = "play-" + [guid]::NewGuid().ToString('N').Substring(0, 12)
$jwtSecret = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$connection = "Host=127.0.0.1;Port=$PostgresPort;Database=$database;Username=postgres;Password=$dbPassword"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

$api = $null
$envNames = 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ConnectionStrings__Postgres', 'Jwt__Secret', 'Redis__Enabled',
    'Database__AutoMigrate', 'Database__SeedDevelopmentData', 'Catalog__SeedWd', 'Catalog__SeedCarCables', 'Catalog__SeedAdomo',
    'Catalog__SeedDepartmentLayout', 'Firebase__Enabled', 'Payments__Provider', 'Storage__Provider'
try {
    Step "PostgreSQL in Docker ($container)"
    docker rm -f $container 2>$null | Out-Null
    docker run -d --name $container -e POSTGRES_PASSWORD=$dbPassword -e POSTGRES_DB=$database `
        -p "127.0.0.1:${PostgresPort}:5432" postgres:16-alpine | Out-Null
    $ready = $false
    foreach ($i in 1..60) {
        docker exec $container pg_isready -U postgres -d $database 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw "PostgreSQL did not start." }

    $publish = Join-Path $work 'api'
    if (-not $SkipPublish -or -not (Test-Path (Join-Path $publish 'AuctionPlatform.Api.dll'))) {
        Step "Building the API and the tool (Release)"
        dotnet publish (Join-Path $root 'src\AuctionPlatform.Api\AuctionPlatform.Api.csproj') -c Release -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing the API failed." }
    }
    dotnet build (Join-Path $root 'tools\AuctionLoadTest\AuctionLoadTest.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Building the tool failed." }
    $tool = Join-Path $root 'tools\AuctionLoadTest\bin\Release\net10.0\AuctionLoadTest.dll'

    Step "Starting the local server on port $ApiPort"
    $env:ASPNETCORE_ENVIRONMENT = 'LoadTest'
    # Every address of this machine, so a phone on the same Wi-Fi can reach it.
    $env:ASPNETCORE_URLS = "http://0.0.0.0:$ApiPort"
    $env:ConnectionStrings__Postgres = $connection
    $env:Jwt__Secret = $jwtSecret
    $env:Redis__Enabled = 'false'
    $env:Database__AutoMigrate = 'true'
    $env:Database__SeedDevelopmentData = 'false'
    $env:Catalog__SeedWd = 'false'
    $env:Catalog__SeedCarCables = 'false'
    $env:Catalog__SeedAdomo = 'false'
    $env:Catalog__SeedDepartmentLayout = 'false'
    $env:Firebase__Enabled = 'false'
    $env:Payments__Provider = 'SANDBOX'
    $env:Storage__Provider = 'Local'
    $api = Start-Process dotnet -ArgumentList @("`"$(Join-Path $publish 'AuctionPlatform.Api.dll')`"") -WorkingDirectory $publish `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'api.log') -RedirectStandardError (Join-Path $work 'api.err.log')

    $up = $false
    foreach ($i in 1..120) {
        try {
            $health = Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/health" -UseBasicParsing -TimeoutSec 5
            if ($health.StatusCode -eq 200) { $up = $true; break }
        } catch { }
        if ($api.HasExited) { throw "The server stopped while starting; see $work\api.log" }
        Start-Sleep -Seconds 1
    }
    if (-not $up) { throw "The server did not become healthy; see $work\api.log" }

    $lan = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
        Select-Object -ExpandProperty IPAddress
    Step "Open the app"
    Write-Host "    This computer:  http://127.0.0.1:$ApiPort/app/"
    foreach ($ip in $lan) { Write-Host "    Phone (same Wi-Fi):  http://${ip}:$ApiPort/app/" }
    Write-Host "    Sign in with any phone number; the code fills itself in."

    dotnet $tool play --base-url "http://127.0.0.1:$ApiPort/" --db $connection --jwt-secret $jwtSecret `
        --bots $Bots --rounds $Rounds --duration $DurationSeconds --countdown $Countdown `
        --rate $Rate --burst-rate $BurstRate --increment $Increment
    exit $LASTEXITCODE
}
finally {
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
    foreach ($name in $envNames) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    docker rm -f $container 2>$null | Out-Null
}
