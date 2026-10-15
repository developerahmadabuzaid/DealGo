# Builds the Flutter app for the web and puts it where the API serves it, at /app/.
#
# The result is an installable PWA: open https://<host>/app/ on a phone and add it to the home
# screen. The app talks to whichever host served it, so the same build works on localhost,
# through a tunnel and in production.
#
#   pwsh scripts/build-pwa.ps1

$ErrorActionPreference = 'Stop'

# Brotli arrived with PowerShell 7; Windows PowerShell 5 cannot compress the build.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    throw 'Run this with PowerShell 7: pwsh scripts/build-pwa.ps1'
}

$root = Split-Path $PSScriptRoot -Parent
$mobile = Join-Path $root 'mobile'
$output = Join-Path $root 'src/AuctionPlatform.Api/wwwroot/app'

Push-Location $mobile
try {
    # Flutter leaves earlier output in build/web; clearing it keeps stale files out of the copy.
    if (Test-Path 'build/web') { Get-ChildItem -Path 'build/web' -Force | Remove-Item -Recurse -Force }

    # Flutter's own service worker no longer caches anything, so it is switched off and
    # web/sw.js takes over.
    flutter build web --release --base-href=/app/ --pwa-strategy=none
    if ($LASTEXITCODE -ne 0) { throw "flutter build web failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# Emptied rather than deleted — Windows can hold a brief lock on the folder itself — so a file
# dropped from the build does not linger in what is served.
New-Item -ItemType Directory -Force -Path $output | Out-Null
Get-ChildItem -Path $output -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $mobile 'build/web/*') -Destination $output -Recurse

# Flutter still writes a service worker of its own that nothing registers; it would only
# confuse anyone reading what is served.
Remove-Item -Path (Join-Path $output 'flutter_service_worker.js') -Force -ErrorAction SilentlyContinue

# Compressed once here rather than on every request: the app is megabytes of JavaScript and
# WebAssembly, and Brotli at its highest setting takes far too long to do per request. The API
# hands over the .br (or .gz) copy to any browser that accepts it.
$compressible = '.js', '.wasm', '.json', '.css', '.html', '.svg', '.otf', '.ttf', '.map'
$before = 0L
$after = 0L

foreach ($file in Get-ChildItem -Path $output -Recurse -File) {
    if ($compressible -notcontains $file.Extension.ToLowerInvariant()) { continue }
    if ($file.Length -lt 1024) { continue }

    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $before += $bytes.Length

    foreach ($kind in 'br', 'gz') {
        $target = "$($file.FullName).$kind"
        $out = [System.IO.File]::Create($target)
        try {
            $stream = if ($kind -eq 'br') {
                [System.IO.Compression.BrotliStream]::new($out, [System.IO.Compression.CompressionLevel]::SmallestSize)
            } else {
                [System.IO.Compression.GZipStream]::new($out, [System.IO.Compression.CompressionLevel]::SmallestSize)
            }
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
        finally { $out.Dispose() }

        if ($kind -eq 'br') { $after += (Get-Item $target).Length }
    }
}

if ($before -gt 0) {
    $mbBefore = [math]::Round($before / 1MB, 1)
    $mbAfter = [math]::Round($after / 1MB, 1)
    Write-Host "Compressed $mbBefore MB down to $mbAfter MB with Brotli"
}

# A fresh cache name per build, so an update replaces the old files instead of sitting behind them.
$worker = Join-Path $output 'sw.js'
$buildId = Get-Date -Format 'yyyyMMddHHmmss'
(Get-Content $worker -Raw).Replace('__BUILD_ID__', $buildId) | Set-Content $worker -NoNewline

Write-Host "PWA built into $output (build $buildId) — serve the API and open /app/"
