#!/usr/bin/env bash
# Erstellt das Installationspaket für den Windows-Server unter dist/Inventory (inklusive Runner und Installationsskript).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/Inventory"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

rm -rf "$OUT"
# Self-contained: auf dem Server muss keine .NET-Laufzeit installiert sein.
dotnet publish "$ROOT/src/Inventory.Server" -c Release -r win-x64 --self-contained true -o "$OUT/app"
cp "$ROOT/scripts/install-service.ps1" "$OUT/"
echo "Fertig: $OUT – Ordner auf den Server kopieren und dort install-service.ps1 als Administrator ausführen."
