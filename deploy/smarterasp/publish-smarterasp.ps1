<#
.SYNOPSIS
    Builds the uploadable site for SmarterASP.NET shared hosting.

.DESCRIPTION
    Publishes the API, applies the hosting-specific configuration, and leaves one folder to upload
    by FTP plus the SQL script that creates the database. It stops rather than producing a
    half-configured package: a folder carrying a placeholder connection string or a placeholder
    signing key looks exactly like a working deployment until the first request, which is the
    worst possible moment to discover it.

.PARAMETER ConnectionString
    Npgsql connection string for the PostgreSQL database created in the host's Database Manager.

.PARAMETER JwtSecret
    Signing key, at least 32 characters. Generate one; do not write one out by hand.

.PARAMETER TawkedApiKey
    Key from the Keys tab of the approved application at tawked.com. Production refuses to start
    without it, so there is no way to omit this one.

.PARAMETER SiteUrl
    The site's public origin, https://example.com. CORS is not needed by the site's own pages, and
    only matters for browser clients served from somewhere else.

.PARAMETER BootstrapAdminPhone
    The Saudi mobile number of the first administrator, as they signed up with it in the app.
    Staff sign in to /admin/ with their number, so this is usually the one to set rather than
    -BootstrapAdminEmail. Either works; they are read only while no super administrator exists.

.PARAMETER AllowPlaceholders
    Builds a package with placeholders left in place. For checking the package before the
    credentials exist, and for nothing else.

.PARAMETER SkipPwa
    Leaves out the Flutter web build. It is about 68 MB of the roughly 72 MB uploaded folder, and
    is only needed by clients that install the site as an app in a phone browser.

.PARAMETER Zip
    Also writes artifacts\smarterasp\site.zip, holding the contents of the site folder rather than
    the folder itself, so the hosting control panel can upload it in one transfer and extract it
    straight into the document root.

.EXAMPLE
    .\publish-smarterasp.ps1 `
        -ConnectionString 'Host=sql123.smarterasp.net;Port=5432;Database=u12345_auctions;Username=u12345_admin;Password=…;SSL Mode=Require' `
        -JwtSecret (New-Guid).Guid + (New-Guid).Guid `
        -TawkedApiKey 'tk_live_…' `
        -SiteUrl 'https://auctions.example.com'
#>
[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$JwtSecret,
    [string]$TawkedApiKey,
    [string]$PaymentWebhookSecret = 'not-configured',
    [string]$BootstrapAdminEmail,
    [string]$BootstrapAdminPhone,
    [string]$SiteUrl,
    [string]$OutputPath,
    [switch]$AllowPlaceholders,
    [switch]$SkipPwa,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectFile = Join-Path $repoRoot 'src\AuctionPlatform.Api\AuctionPlatform.Api.csproj'
$templateDir = $PSScriptRoot

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'artifacts\smarterasp'
}

$siteFolder = Join-Path $OutputPath 'site'

<#
    Saved values, then the arguments.

    A live signing key and a live SMS key belong in a file that is not in version control, not in
    the command line of every rebuild — the command line is visible to anything on the machine and
    lands in shell history. So local-secrets.ps1 supplies the defaults, and an argument given on the
    command line still wins, which is what makes it possible to change one value for one build
    without editing that file.
#>
$boundArguments = @{}
foreach ($name in $PSBoundParameters.Keys) {
    $boundArguments[$name] = $PSBoundParameters[$name]
}

$secretsFile = Join-Path $PSScriptRoot 'local-secrets.ps1'

if (Test-Path $secretsFile) {
    . $secretsFile

    foreach ($name in $boundArguments.Keys) {
        Set-Variable -Name $name -Value $boundArguments[$name]
    }
}

function Write-Step($message) {
    Write-Host "`n==> $message" -ForegroundColor Cyan
}

function Write-Detail($message) {
    Write-Host "    $message" -ForegroundColor DarkGray
}

function Resolve-Placeholder([string]$value, [string]$placeholder, [string]$label) {
    if ($value -and $value -ne $placeholder) { return $value }

    if ($AllowPlaceholders) { return $placeholder }

    throw @"
$label has no usable value.

  Set it in deploy\smarterasp\local-secrets.ps1 (which is git-ignored), or pass -$label on the
  command line, or add -AllowPlaceholders to build a package with placeholders left in place.
"@
}

# --------------------------------------------------------------------- configuration

Write-Step 'Collecting configuration'

$connectionString = Resolve-Placeholder $ConnectionString '__CONNECTION_STRING__' 'ConnectionString'

$jwtSecret = Resolve-Placeholder $JwtSecret '__JWT_SECRET__' 'JwtSecret'

if ($jwtSecret -ne '__JWT_SECRET__' -and $jwtSecret.Length -lt 32) {
    throw "JwtSecret is $($jwtSecret.Length) characters. The API requires at least 32, and refuses to start on a shorter one."
}

$tawkedApiKey = Resolve-Placeholder $TawkedApiKey '__TAWKED_API_KEY__' 'TawkedApiKey'

$siteUrl = Resolve-Placeholder $SiteUrl '__SITE_ORIGIN__' 'SiteUrl'

# Optional rather than required: it is only read when the database holds no super administrator
# yet, and it promotes an account that already exists under that address — which cannot have
# happened before the first upload. Leaving it empty leaves /admin/ reachable by nobody.
$bootstrapAdminEmail = if ($BootstrapAdminEmail) { $BootstrapAdminEmail.Trim() } else { '' }
$bootstrapAdminPhone = if ($BootstrapAdminPhone) { $BootstrapAdminPhone.Trim() } else { '' }

$webConfig = Get-Content (Join-Path $templateDir 'web.config.template') -Raw
$webConfig = $webConfig.Replace('__CONNECTION_STRING__', $connectionString)
$webConfig = $webConfig.Replace('__JWT_SECRET__', $jwtSecret)
$webConfig = $webConfig.Replace('__TAWKED_API_KEY__', $tawkedApiKey)
$webConfig = $webConfig.Replace('__PAYMENT_WEBHOOK_SECRET__', $PaymentWebhookSecret)
$webConfig = $webConfig.Replace('__BOOTSTRAP_ADMIN_EMAIL__', $bootstrapAdminEmail)
$webConfig = $webConfig.Replace('__BOOTSTRAP_ADMIN_PHONE__', $bootstrapAdminPhone)

# Read from the template, written out under the name the application loads. The two names differ
# on purpose: .gitignore already excludes any appsettings.Production.json, which is right for the
# uploaded file and wrong for a template that holds nothing secret.
$productionSettings = Get-Content (Join-Path $templateDir 'appsettings.Production.template.json') -Raw
$productionSettings = $productionSettings.Replace('__SITE_ORIGIN__', $siteUrl)

# --------------------------------------------------------------------- guard

<#
    Guard against locked IIS sections.

    A shared host locks most of system.webServer in its own applicationHost.config. A web.config
    that touches a locked section — requestFiltering and httpProtocol are the usual two — is not
    partially applied, it is rejected whole, and IIS answers every request with 500.19. The
    symptom is indistinguishable from a broken application: an empty stdout log, a dead site, and
    nothing in the log to say why, because the application never started to write one.

    So this fails the build instead of shipping a package that cannot run anywhere.
#>
$lockedSections = @('requestFiltering', 'httpProtocol', 'system.webServer/handlers', 'system.webSecurity')

foreach ($section in $lockedSections) {
    if ($webConfig -match "<$section[\s>]") {
        throw @"
The generated web.config contains a <$section> section.

Shared hosts lock these in their own configuration. IIS then rejects the entire file with
error 500.19, the application never starts, and the reason appears nowhere in the logs.
"@
    }
}

# --------------------------------------------------------------------- publish

Write-Step 'Publishing the API'

if (Test-Path $siteFolder) {
    Write-Detail 'Removing the previous output'
    Remove-Item $siteFolder -Recurse -Force
}

& dotnet publish $projectFile `
    --configuration Release `
    --output $siteFolder `
    --nologo `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# Written after the publish so nothing the SDK emits is overwritten by it.
Set-Content -Path (Join-Path $siteFolder 'web.config') -Value $webConfig -Encoding UTF8
Set-Content -Path (Join-Path $siteFolder 'appsettings.Production.json') -Value $productionSettings -Encoding UTF8

# Created now so the application pool identity already owns it, rather than having to create it on
# a first request during a live auction.
New-Item -ItemType Directory -Force -Path (Join-Path $siteFolder 'logs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $siteFolder 'wwwroot\uploads') | Out-Null

if ($SkipPwa) {
    $pwaFolder = Join-Path $siteFolder 'wwwroot\app'
    if (Test-Path $pwaFolder) {
        Write-Step 'Removing the web app'
        Remove-Item $pwaFolder -Recurse -Force
        Write-Detail 'Anyone opening /app/ will get the site index instead of the installable app'
    }
}

# --------------------------------------------------------------------- archive

<#
    One file instead of several hundred.

    A shared host's file manager takes a single upload and extracts it, which beats FTP on 258
    files: the transfer happens once, there is no resume support to get wrong, and a half-finished
    upload cannot leave a working site missing some of its own DLLs.

    The archive holds the *contents* of the site folder rather than the folder itself. That is the
    whole point of it — extracting must drop web.config and AuctionPlatform.Api.dll straight into
    the document root, not into a new site\ subfolder one level down.
#>
if ($Zip) {
    Write-Step 'Compressing the site'

    $zipPath = Join-Path $OutputPath 'site.zip'

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    <#
        Forward slashes, not backslashes.

        The ZIP specification (APPNOTE 4.4.17.1) requires "/" as the path separator inside
        every entry. .NET's CreateFromDirectory writes backslashes on Windows, which works for
        .NET's own extractor but can silently produce wrong folder names in tools that follow
        the spec. Every file is added individually so the name can be corrected before it is
        stored.
    #>
    $root = (Resolve-Path $siteFolder).Path.TrimEnd('\')

    $archive = [System.IO.Compression.ZipFile]::Open(
        $zipPath,
        [System.IO.Compression.ZipArchiveMode]::Create)

    try {
        Get-ChildItem $siteFolder -Recurse -File | ForEach-Object {
            $relativePath = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $_.FullName,
                $relativePath,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }

    $zipSize = (Get-Item $zipPath).Length
    $rawSize = (Get-ChildItem $siteFolder -Recurse -File | Measure-Object -Sum Length).Sum

    Write-Detail ("{0} -> {1:N1} MB" -f
        ([math]::Round($zipSize / 1MB, 1)), ([math]::Round($rawSize / 1MB, 1)))
}

# --------------------------------------------------------------------- database script

Write-Step 'Writing the database script'

$sqlPath = Join-Path $OutputPath '01-database-schema.sql'

& dotnet ef migrations script `
    --idempotent `
    --no-build `
    --configuration Release `
    --project (Join-Path $repoRoot 'src\AuctionPlatform.Infrastructure') `
    --startup-project $projectFile `
    --output $sqlPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet ef migrations script failed with exit code $LASTEXITCODE."
}

# --------------------------------------------------------------------- report

$siteSize  = (Get-ChildItem $siteFolder -Recurse -File | Measure-Object -Sum Length).Sum
$fileCount = (Get-ChildItem $siteFolder -Recurse -File).Count

Write-Step 'Done'

Write-Detail "Site:  $siteFolder"
Write-Detail ("Size:  {0:N1} MB across {1:N0} files" -f ($siteSize / 1MB), $fileCount)
Write-Detail "SQL:   $sqlPath"

if ($siteSize -gt 100MB) {
    Write-Warning "The folder is larger than most FTP clients handle comfortably. Consider -SkipPwa."
}

$placeholders = Select-String -Path (Join-Path $siteFolder 'web.config'), (Join-Path $siteFolder 'appsettings.Production.json') `
    -Pattern '__[A-Z_]+__' -AllMatches

if ($placeholders) {
    Write-Warning 'This package still contains placeholders and will not run as it stands:'
    $placeholders | ForEach-Object {
        Write-Warning ("  {0}: {1}" -f $_.Filename, ($_.Matches | ForEach-Object { $_.Value } | Select-Object -Unique) -join ' ')
    }
}
elseif ($PaymentWebhookSecret -eq 'not-configured') {
    Write-Warning 'Payments__WebhookSecret was left at its default. Payment webhooks will be rejected until it is set.'
}
else {
    Write-Host "`nPackage is complete. See deploy\smarterasp\DEPLOYMENT-AR.md for the upload steps." -ForegroundColor Green
}

if (-not $bootstrapAdminEmail -and -not $bootstrapAdminPhone) {
    Write-Warning @'

No Bootstrap__SuperAdminPhone (or Bootstrap__SuperAdminEmail) was set, so nobody will be able to
sign in to /admin/ on a fresh database.

It is two steps, in this order: sign up in the app with that phone number first, and only then
set the setting and restart — the application promotes an account that already exists, so a
number nobody has signed up with is silently ignored.
'@
}