#!/usr/bin/env bash
# macOS. One-command setup: checks/installs .NET 8 SDK and Node 20+, installs npm packages, restores NuGet packages and builds the frontend.
# Run ./for-mac/run.sh afterwards. Safe to re-run: anything already installed is skipped.
set -euo pipefail
cd "$(dirname "$0")/.."

say() { printf '\n==> %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

# Same lookup as run.sh: a Homebrew/user-local .NET may exist without being on PATH.
find_dotnet() {
  have dotnet && return 0
  for d in /opt/homebrew/opt/dotnet@8 /usr/local/opt/dotnet@8 /usr/local/share/dotnet "$HOME/.dotnet"; do
    if [ -x "$d/bin/dotnet" ]; then export PATH="$d/bin:$PATH" DOTNET_ROOT="$d/libexec"; return 0; fi
    if [ -x "$d/dotnet" ]; then export PATH="$d:$PATH" DOTNET_ROOT="$d"; return 0; fi
  done
  return 1
}
dotnet_ok() { find_dotnet && dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; }
node_ok() { have node && [ "$(node -v | sed 's/^v//; s/\..*//')" -ge 20 ]; }

# ---- .NET 8 SDK
say ".NET 8 SDK"
if dotnet_ok; then
  echo "ya instalado: $(dotnet --version)"
elif have brew; then
  brew install dotnet@8
  dotnet_ok || { echo "dotnet@8 se instaló pero no se encuentra; abre una terminal nueva y repite."; exit 1; }
else
  echo "Falta Homebrew (https://brew.sh). Instala el SDK de .NET 8 desde https://dotnet.microsoft.com/download/dotnet/8.0 y vuelve a ejecutar ./for-mac/install.sh"; exit 1
fi

# ---- Node 20+
say "Node 20+"
if node_ok; then
  echo "ya instalado: $(node -v)"
elif have brew; then
  brew install node@20 && brew link --overwrite --force node@20 >/dev/null 2>&1 || true
  node_ok || { echo "Node se instaló pero no está en el PATH; abre una terminal nueva y repite."; exit 1; }
else
  echo "Instala Node 20 o superior (https://nodejs.org o nvm) y vuelve a ejecutar ./for-mac/install.sh"; exit 1
fi

# ---- npm packages (skipped when node_modules is already in sync with the lockfile)
say "Paquetes npm (web/)"
if [ -f web/node_modules/.package-lock.json ] && [ web/node_modules/.package-lock.json -nt web/package-lock.json ]; then
  echo "node_modules ya está al día"
else
  (cd web && npm ci)
fi

# ---- NuGet + frontend build
say "Paquetes NuGet"
dotnet restore ExpenseIntelligence.sln

say "Compilando el frontend (src/ExpenseIntelligence.Api/wwwroot)"
(cd web && npx ng build)

say "Listo. Arranca con ./for-mac/run.sh  (http://localhost:5080)"
