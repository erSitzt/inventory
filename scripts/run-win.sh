#!/usr/bin/env bash
# Baut den Server für Windows und startet ihn als Windows-Prozess (WSL-Interop).
# Echte Remote-Zugriffe (SMB/SCM/WinRM, Kerberos/NTLM) funktionieren nur so, nicht aus WSL heraus.
# Ausgabe und Daten liegen bewusst auf NTFS: SQLite-Sperren über \\wsl.localhost sind unzuverlässig.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Standard: %LOCALAPPDATA%\InventoryDev des Windows-Benutzers (ohne Adminrechte beschreibbar)
WIN_LOCALAPPDATA="$(cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%' 2>/dev/null | tr -d '\r')"
OUT="${INVENTORY_WIN_DIR:-$(wslpath -u "$WIN_LOCALAPPDATA")/InventoryDev}"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$OUT/app" "$OUT/app/runner" "$OUT/data"
dotnet publish "$ROOT/src/Inventory.Server" -c Release -r win-x64 --self-contained false -o "$OUT/app"
dotnet build "$ROOT/src/Inventory.Runner" -c Release -o "$OUT/app/runner"

cd "$OUT/app"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:5101}"
export Inventory__DataDirectory="$(wslpath -w "$OUT/data")"
export WSLENV="ASPNETCORE_ENVIRONMENT:ASPNETCORE_URLS:Inventory__DataDirectory${WSLENV:+:$WSLENV}"
exec ./Inventory.Server.exe "$@"
