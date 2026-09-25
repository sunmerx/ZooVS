# Build cline JS artifacts (daemon + standalone webview) and copy to vsix/assets.
# Usage: powershell -File build-js.ps1 [-SkipInstall]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
$Repo = Join-Path $PSScriptRoot "..\cline"
$VscodeApp = Join-Path $Repo "apps\vscode"
$Assets = Join-Path $PSScriptRoot "assets"

if (-not $SkipInstall) {
    Write-Host "== [1/5] bun install ==" -ForegroundColor Cyan
    Push-Location $Repo
    bun install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "bun install failed" }
    Pop-Location
}

Write-Host "== [2/5] generate TS protos ==" -ForegroundColor Cyan
Push-Location $VscodeApp
bun run protos
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "protos failed" }
Pop-Location

Write-Host "== [3/5] build standalone webview ==" -ForegroundColor Cyan
$env:PLATFORM = "standalone"
Push-Location (Join-Path $VscodeApp "webview-ui")
bun run build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "webview build failed" }
Pop-Location
Remove-Item Env:PLATFORM -ErrorAction SilentlyContinue

Write-Host "== [4/5] esbuild standalone daemon ==" -ForegroundColor Cyan
Push-Location $VscodeApp
bun esbuild.mjs --standalone
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "esbuild failed" }
Pop-Location

Write-Host "== [5/5] assemble assets/daemon ==" -ForegroundColor Cyan
$DaemonOut = Join-Path $Assets "daemon"
if (Test-Path $DaemonOut) { Remove-Item $DaemonOut -Recurse -Force }
New-Item -ItemType Directory -Path $DaemonOut | Out-Null

Copy-Item (Join-Path $VscodeApp "dist-standalone\cline-core.js") $DaemonOut

# Minimal dependency install per runtime-files manifest (isolated node_modules)
Copy-Item (Join-Path $VscodeApp "standalone\runtime-files\package.json") $DaemonOut
Push-Location $DaemonOut
npm install --omit=dev --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "daemon npm install failed" }
Pop-Location

# ripgrep binary (cline-core expects rg next to itself)
Push-Location $VscodeApp
bun run download-ripgrep
if ($LASTEXITCODE -ne 0) { Pop-Location; Write-Warning "download-ripgrep failed" }
Pop-Location
$Rg = Get-ChildItem -Path (Join-Path $VscodeApp "dist-standalone") -Filter "rg.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($Rg) {
    Copy-Item $Rg.FullName $DaemonOut
} else {
    Write-Warning "rg.exe not found in dist-standalone; code-search tool will be unavailable"
}

# extension metadata directory (initializeContext reads <cwd>/extension/package.json)
New-Item -ItemType Directory -Force -Path (Join-Path $DaemonOut "extension") | Out-Null
Copy-Item (Join-Path $VscodeApp "package.json") (Join-Path $DaemonOut "extension\package.json") -Force

# vscode stub module must live in node_modules (same as package-standalone.mjs)
New-Item -ItemType Directory -Force -Path (Join-Path $DaemonOut "node_modules") | Out-Null
Move-Item -Force (Join-Path $DaemonOut "vscode") (Join-Path $DaemonOut "node_modulesscode") -ErrorAction SilentlyContinue

# webview build output
$WebOut = Join-Path $Assets "webview"
if (Test-Path $WebOut) { Remove-Item $WebOut -Recurse -Force }
Copy-Item (Join-Path $VscodeApp "webview-ui\build") $WebOut -Recurse

Write-Host "== JS artifacts done ==" -ForegroundColor Green
Write-Host "  daemon: $DaemonOut"
Write-Host "  webview: $WebOut"
