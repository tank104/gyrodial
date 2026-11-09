<#
.SYNOPSIS
  Builds the Gyrodial firmware and the Windows app, and packages both.

.DESCRIPTION
  1. Builds the firmware with ESP-IDF (idf.py build in firmware/).
  2. Copies the flash images + an ESP Web Tools manifest into
     windows/src/Gyrodial.App/firmware (the app's installer) and web/firmware (the browser installer).
  3. Publishes the app (self-contained, win-x64) with gyrodial-hook.exe next to it into dist/Gyrodial,
     and zips it to dist/Gyrodial-win-x64.zip.

  ESP-IDF v5.5 must be installed. If idf.py isn't already on PATH (an "ESP-IDF PowerShell"
  window), the script loads it from -IdfPath, $env:IDF_PATH, or ~/esp/esp-idf.

.EXAMPLE
  ./scripts/build.ps1
  ./scripts/build.ps1 -SkipFirmware        # app only, reusing the last firmware build
  ./scripts/build.ps1 -IdfPath C:\esp\v5.5.3\esp-idf
#>
param(
    [switch]$SkipFirmware,
    [switch]$SkipApp,
    [string]$IdfPath = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# Puts idf.py on PATH by running ESP-IDF's own export script, unless it's there already.
function Enter-Idf {
    if (Get-Command idf.py -ErrorAction SilentlyContinue) { return }
    $candidates = @($IdfPath, $env:IDF_PATH, (Join-Path $env:USERPROFILE "esp/esp-idf")) | Where-Object { $_ }
    $idf = $candidates | Where-Object { Test-Path (Join-Path $_ "export.ps1") } | Select-Object -First 1
    if (-not $idf) { throw "ESP-IDF not found. Install ESP-IDF v5.5, or pass -IdfPath <esp-idf folder>." }
    # export.ps1 refuses to run inside an MSYS/Git Bash environment
    Remove-Item Env:MSYSTEM -ErrorAction SilentlyContinue
    Write-Host "Loading ESP-IDF from $idf" -ForegroundColor DarkGray
    . (Join-Path $idf "export.ps1") | Out-Null
    if (-not (Get-Command idf.py -ErrorAction SilentlyContinue)) { throw "ESP-IDF's export.ps1 didn't put idf.py on PATH." }
}

# ---- firmware
$fwRoot = Join-Path $root "firmware"
$fwBuild = Join-Path $fwRoot "build"
if (-not $SkipFirmware) {
    Write-Host "Building firmware..." -ForegroundColor Cyan
    Enter-Idf
    Push-Location $fwRoot
    try { Invoke-Checked "Firmware build" { idf.py build } } finally { Pop-Location }
}

# flasher_args.json is ESP-IDF's own list of images and offsets: bootloader at 0x0, the partition
# table at 0x8000, the OTA data (start the first app slot) at 0xe000, and the app at 0x10000.
$flasherArgs = Join-Path $fwBuild "flasher_args.json"
if (Test-Path $flasherArgs) {
    $flash = Get-Content $flasherArgs -Raw | ConvertFrom-Json
    $desc = Get-Content (Join-Path $fwBuild "project_description.json") -Raw | ConvertFrom-Json
    $version = $desc.project_version
    $parts = @()
    foreach ($p in $flash.flash_files.PSObject.Properties | Sort-Object { [Convert]::ToInt32($_.Name, 16) }) {
        $parts += [ordered]@{ path = (Split-Path $p.Value -Leaf); offset = [Convert]::ToInt32($p.Name, 16); source = (Join-Path $fwBuild $p.Value) }
    }
    $manifest = [ordered]@{
        name = "Gyrodial"
        version = $version
        new_install_prompt_erase = $true
        builds = @(@{
            chipFamily = "ESP32-S3"
            parts = @($parts | ForEach-Object { [ordered]@{ path = $_.path; offset = $_.offset } })
        })
    } | ConvertTo-Json -Depth 5

    foreach ($dest in (Join-Path $root "windows/src/Gyrodial.App/firmware"), (Join-Path $root "web/firmware")) {
        if (Test-Path $dest) { Remove-Item (Join-Path $dest "*") -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        foreach ($part in $parts) { Copy-Item $part.source (Join-Path $dest $part.path) -Force }
        Set-Content -Path (Join-Path $dest "manifest.json") -Value $manifest -Encoding utf8
    }
    Write-Host "Firmware $version copied to the app and the web installer." -ForegroundColor Green
} else {
    Write-Warning "No firmware build found; the app will be packaged without firmware images."
}

# ---- app
if (-not $SkipApp) {
    $dist = Join-Path $root "dist/Gyrodial"
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    Write-Host "Publishing the app..." -ForegroundColor Cyan
    Invoke-Checked "App publish" {
        dotnet publish (Join-Path $root "windows/src/Gyrodial.App/Gyrodial.App.csproj") -c $Configuration -r win-x64 --self-contained true -o $dist
    }
    # gyrodial-hook.exe runs from Claude Code, which may start it before the app: make it stand alone.
    # The app publish brought the framework-dependent build along; replace it with the single file.
    Remove-Item (Join-Path $dist "gyrodial-hook.*") -Force -ErrorAction SilentlyContinue
    Invoke-Checked "gyrodial-hook publish" {
        dotnet publish (Join-Path $root "windows/src/GyrodialHook/GyrodialHook.csproj") -c $Configuration -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:PublishTrimmed=true -o $dist
    }
    $zip = Join-Path $root "dist/Gyrodial-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $zip
    Write-Host "Done: $zip" -ForegroundColor Green
}
