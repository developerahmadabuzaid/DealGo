<#
.SYNOPSIS
    Runs the bidding load test against a production-like server on this machine.

.DESCRIPTION
    Starts a throwaway PostgreSQL in Docker, publishes the API in Release and runs it the way
    SmarterASP does (Redis off, so locks, rate limits and SignalR are in-process; demo data off),
    then runs `AuctionLoadTest run` for each participant count and writes the report.

    Nothing here touches dealgosa.com or the production database. The container, the API and the
    optional latency relay are stopped at the end; pass -KeepDatabase to leave PostgreSQL running.

.PARAMETER Participants
    Participant counts to run, one auction each. Default 13, 50, 100, 400.

.PARAMETER DbDelayMs
    Delay added each way between the API and PostgreSQL by a relay, to imitate a database that is
    on another machine (a round trip costs twice this). 0 means a direct local connection.

.EXAMPLE
    .\tools\AuctionLoadTest\run-local.ps1
    .\tools\AuctionLoadTest\run-local.ps1 -Participants 400 -DbDelayMs 2
    .\tools\AuctionLoadTest\run-local.ps1 -Mode demo -Close target
#>
[CmdletBinding()]
param(
    [int[]]$Participants = @(13, 50, 100, 400),
    [int]$DurationSeconds = 90,
    [double]$Rate = 0.5,
    [double]$BurstRate = 2.0,
    [int]$BurstSeconds = 15,
    [ValidateSet('paid', 'demo')][string]$Mode = 'paid',
    [ValidateSet('time', 'target')][string]$Close = 'time',
    [int]$DbDelayMs = 0,
    [int]$PostgresMaxConnections = 200,
    [int]$ApiPort = 5199,
    [int]$PostgresPort = 5462,
    [string]$OutputPath,
    [switch]$KeepDatabase,
    # Also runs `monitor` beside the load test; it watches the first auction, as it would a live one.
    [switch]$WithMonitor
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $OutputPath) { $OutputPath = Join-Path $root "artifacts\loadtest\$runId" }
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path

$container = "dealgo-loadtest-pg"
$database = "auctions_loadtest"
$dbPassword = "loadtest-" + [guid]::NewGuid().ToString('N').Substring(0, 12)
$jwtSecret = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$dbPort = if ($DbDelayMs -gt 0) { $PostgresPort + 1 } else { $PostgresPort }
$connection = "Host=127.0.0.1;Port=$dbPort;Database=$database;Username=postgres;Password=$dbPassword"
$directConnection = "Host=127.0.0.1;Port=$PostgresPort;Database=$database;Username=postgres;Password=$dbPassword"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

$api = $null
$relay = $null
$monitor = $null
try {
    Step "PostgreSQL in Docker ($container, max_connections $PostgresMaxConnections)"
    docker rm -f $container 2>$null | Out-Null
    docker run -d --name $container -e POSTGRES_PASSWORD=$dbPassword -e POSTGRES_DB=$database `
        -p "127.0.0.1:${PostgresPort}:5432" postgres:16-alpine -c "max_connections=$PostgresMaxConnections" | Out-Null
    $ready = $false
    foreach ($i in 1..60) {
        docker exec $container pg_isready -U postgres -d $database 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw "PostgreSQL did not start." }

    Step "Building the API and the load test (Release)"
    $publish = Join-Path $OutputPath 'api'
    dotnet publish (Join-Path $root 'src\AuctionPlatform.Api\AuctionPlatform.Api.csproj') -c Release -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publishing the API failed." }
    dotnet build (Join-Path $root 'tools\AuctionLoadTest\AuctionLoadTest.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Building the load test failed." }
    $tool = Join-Path $root 'tools\AuctionLoadTest\bin\Release\net10.0\AuctionLoadTest.dll'

    if ($DbDelayMs -gt 0) {
        Step "Relay adding $DbDelayMs ms each way between the API and PostgreSQL"
        $relay = Start-Process dotnet -ArgumentList @("`"$tool`"", 'proxy', '--listen', "$dbPort", '--target', "127.0.0.1:$PostgresPort", '--delay-ms', "$DbDelayMs") `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputPath 'relay.log') -RedirectStandardError (Join-Path $OutputPath 'relay.err.log')
        Start-Sleep -Seconds 2
    }

    Step "Starting the API on http://127.0.0.1:$ApiPort (as on SmarterASP: Redis off, no demo data)"
    $env:ASPNETCORE_ENVIRONMENT = 'LoadTest'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$ApiPort"
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
    # Start-Process does not quote its arguments, and this path has spaces in it.
    $api = Start-Process dotnet -ArgumentList @("`"$(Join-Path $publish 'AuctionPlatform.Api.dll')`"") -WorkingDirectory $publish `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputPath 'api.log') -RedirectStandardError (Join-Path $OutputPath 'api.err.log')

    $up = $false
    foreach ($i in 1..120) {
        try {
            $health = Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/health" -UseBasicParsing -TimeoutSec 5
            if ($health.StatusCode -eq 200) { $up = $true; break }
        } catch { }
        if ($api.HasExited) { throw "The API stopped while starting; see $OutputPath\api.log" }
        Start-Sleep -Seconds 1
    }
    if (-not $up) { throw "The API did not become healthy; see $OutputPath\api.log" }

    if ($WithMonitor) {
        Step "Monitor watching the first auction"
        $adminToken = (& dotnet $tool token --db $directConnection --jwt-secret $jwtSecret | Select-Object -Last 1).Trim()
        $monitorOut = Join-Path $OutputPath 'monitor'
        $monitor = Start-Process dotnet -ArgumentList @("`"$tool`"", 'monitor', '--base-url', "http://127.0.0.1:$ApiPort/",
            '--token', $adminToken, '--out', "`"$monitorOut`"", '--interval', '1', '--snapshot-seconds', '2') `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputPath 'monitor.log') -RedirectStandardError (Join-Path $OutputPath 'monitor.err.log')
    }

    Step "Load test: $($Participants -join ', ') participants"
    $environment = "local Release build, PostgreSQL 16 in Docker" + $(if ($DbDelayMs -gt 0) { ", $DbDelayMs ms added each way to the database" } else { ", database on the same machine" })
    dotnet $tool run --base-url "http://127.0.0.1:$ApiPort/" --db $directConnection --jwt-secret $jwtSecret `
        --participants ($Participants -join ',') --duration $DurationSeconds --rate $Rate --burst-rate $BurstRate `
        --burst-seconds $BurstSeconds --mode $Mode --close $Close --out $OutputPath --environment $environment
    $exit = $LASTEXITCODE

    if ($monitor) {
        # It stops by itself ten seconds after its auction closes.
        if (-not $monitor.WaitForExit(120000)) { Write-Warning "The monitor did not finish in time." }
        Write-Host "Monitor report: $OutputPath\monitor\monitor-report.md"
    }

    Step "Done (exit $exit)"
    Write-Host "Report: $OutputPath\report.md"
    exit $exit
}
finally {
    foreach ($process in @($monitor, $api, $relay)) {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    foreach ($name in 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ConnectionStrings__Postgres', 'Jwt__Secret', 'Redis__Enabled',
        'Database__AutoMigrate', 'Database__SeedDevelopmentData', 'Catalog__SeedWd', 'Catalog__SeedCarCables', 'Catalog__SeedAdomo',
        'Catalog__SeedDepartmentLayout', 'Firebase__Enabled', 'Payments__Provider', 'Storage__Provider') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
    if (-not $KeepDatabase) { docker rm -f $container 2>$null | Out-Null }
}
