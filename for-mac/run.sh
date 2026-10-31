#!/usr/bin/env bash
# One-command start: builds the Angular app into the API's wwwroot (if needed) and runs the API on http://localhost:5080
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1; then
  for d in /opt/homebrew/opt/dotnet@8 /usr/local/share/dotnet "$HOME/.dotnet"; do
    [ -x "$d/bin/dotnet" ] && export PATH="$d/bin:$PATH" DOTNET_ROOT="$d/libexec" && break
    [ -x "$d/dotnet" ] && export PATH="$d:$PATH" DOTNET_ROOT="$d" && break
  done
fi
if [ ! -f src/ExpenseIntelligence.Api/wwwroot/index.html ]; then
  (cd web && npm ci && npx ng build)
fi
exec dotnet run --project src/ExpenseIntelligence.Api -c Release
