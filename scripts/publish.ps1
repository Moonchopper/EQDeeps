# Builds the SPA, publishes EQDeeps, and optionally compiles the installer.
# Usage: pwsh scripts/publish.ps1 [-Runtime win-x64] [-Version 0.1.0] [-Installer]
#
# The output is a folder, not a single-file exe: Inno Setup installs a normal
# application directory, and the in-app updater replaces that directory in
# place (ADR-010). Single-file publishing would also re-extract ~180 MB to temp
# on every launch for no benefit once there is a real installer.
param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    Write-Host "== Building SPA =="
    npm --prefix ui run build
    if ($LASTEXITCODE -ne 0) { throw "UI build failed" }

    Write-Host "== Publishing $Runtime v$Version =="
    dotnet publish src/EQDeeps.Server -c Release -r $Runtime --self-contained `
        -p:Version=$Version `
        -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    # Attribution ships with every distributed copy (see NOTICE).
    Copy-Item NOTICE "artifacts/$Runtime/NOTICE.txt" -Force

    if ($Installer) {
        Write-Host "== Compiling installer =="
        # ISCC is rarely on PATH. The LocalAppData path is where an unelevated
        # `winget install JRSoftware.InnoSetup` puts it, which is the common case.
        $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
        if (-not $iscc) {
            $iscc = @(
                (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
                (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
                (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
            ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $iscc) {
            throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)."
        }

        & $iscc "/DAppVersion=$Version" "/DPayloadDir=..\artifacts\$Runtime" "installer\EQDeeps.iss"
        if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
        Write-Host "Done: artifacts/installer/EQDeeps-Setup-$Version.exe"
    }

    Get-ChildItem "artifacts/$Runtime" |
        Sort-Object Length -Descending |
        Select-Object -First 8 |
        Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
    Write-Host "Done: artifacts/$Runtime/EQDeeps.Server.exe"
}
finally {
    Pop-Location
}
