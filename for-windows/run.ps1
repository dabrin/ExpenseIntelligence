# Windows. Builds the frontend if needed and runs the app on http://localhost:5080
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Falta .NET 8. Ejecuta primero for-windows\install.bat' }
if (-not (Test-Path 'src\ExpenseIntelligence.Api\wwwroot\index.html')) {
  Push-Location web; npm ci; npx ng build; Pop-Location
}
dotnet run --project src/ExpenseIntelligence.Api -c Release
