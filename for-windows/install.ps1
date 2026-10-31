# Windows. One-command setup: checks/installs .NET 8 SDK and Node 20+ (via winget), npm packages, NuGet packages and builds the frontend.
# Run ./run.ps1 (or run.bat) afterwards. Safe to re-run: anything already installed is skipped.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

function Say($m) { Write-Host "`n==> $m" -ForegroundColor Green }
function Have($c) { [bool](Get-Command $c -ErrorAction SilentlyContinue) }
# winget changes PATH for new shells only; reload it so the tools are found in this one.
function Refresh-Path { $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what falló (código $LASTEXITCODE)" } }
function DotnetOk { (Have dotnet) -and ((dotnet --list-sdks) -match '^8\.') }
function NodeOk { (Have node) -and ([int]((node -v).TrimStart('v').Split('.')[0]) -ge 20) }

Say '.NET 8 SDK'
if (-not (DotnetOk)) {
  if (-not (Have winget)) { throw 'No se encontró winget. Instala el SDK de .NET 8 desde https://dotnet.microsoft.com/download/dotnet/8.0 y repite.' }
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
  Refresh-Path
  if (-not (DotnetOk)) { throw '.NET se instaló pero no se encuentra. Abre una terminal nueva y repite.' }
}
Write-Host "listo: $(dotnet --version)"

Say 'Node 20+'
if (-not (NodeOk)) {
  if (-not (Have winget)) { throw 'No se encontró winget. Instala Node 20+ desde https://nodejs.org y repite.' }
  winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements
  Refresh-Path
  if (-not (NodeOk)) { throw 'Node se instaló pero no se encuentra. Abre una terminal nueva y repite.' }
}
Write-Host "listo: $(node -v)"

Say 'Paquetes npm (web/)'
$lock = 'web\package-lock.json'; $installed = 'web\node_modules\.package-lock.json'
if ((Test-Path $installed) -and ((Get-Item $installed).LastWriteTime -gt (Get-Item $lock).LastWriteTime)) {
  Write-Host 'node_modules ya está al día'
} else {
  Push-Location web; npm ci; Check 'npm ci'; Pop-Location
}

Say 'Paquetes NuGet'
dotnet restore ExpenseIntelligence.sln; Check 'dotnet restore'

Say 'Compilando el frontend (src\ExpenseIntelligence.Api\wwwroot)'
Push-Location web; npx ng build; Check 'ng build'; Pop-Location

Say 'Listo. Arranca con for-windows\run.bat  (http://localhost:5080)'
