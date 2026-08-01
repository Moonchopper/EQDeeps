# Builds the SPA and publishes EQDeeps as a self-contained single-file exe.
# Usage: pwsh scripts/publish.ps1 [-Runtime win-x64] [-Version 0.1.0]
param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0"
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
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version `
        -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    Get-ChildItem "artifacts/$Runtime" | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
    Write-Host "Done: artifacts/$Runtime/EQDeeps.Server.exe"
}
finally {
    Pop-Location
}
