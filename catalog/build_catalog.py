#!/usr/bin/env python3
"""Erzeugt catalog.json für die Paketbibliothek aus den Manifesten des winget-Repositories.

Aufruf:  python3 catalog/build_catalog.py <ausgabe.json> [arbeitsordner]

Lädt nur die Manifest-Ordner der gewünschten Pakete (partieller Git-Klon), wählt je Paket die höchste Version
und daraus ein unbeaufsichtigt installierbares Setup (64 Bit bevorzugt). Braucht git und PyYAML.
"""
import datetime
import json
import os
import re
import subprocess
import sys

import yaml

WINGET_REPO = "https://github.com/microsoft/winget-pkgs"
# Reihenfolge = Vorliebe: Windows-Installer-Pakete lassen sich am verlässlichsten still installieren.
PREFERRED_TYPES = ["wix", "msi", "burn", "inno", "nullsoft", "exe"]
DEFAULT_SILENT = {
    "burn": "/quiet /norestart",
    "inno": "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
    "nullsoft": "/S",
}


def run(*args, cwd=None):
    subprocess.run(args, cwd=cwd, check=True, stdout=subprocess.DEVNULL)


def manifest_dir(package_id):
    return "manifests/" + package_id[0].lower() + "/" + "/".join(package_id.split("."))


def version_key(version):
    """Abschnittsweise numerisch (1.10 > 1.9); Buchstaben zählen als Text nach den Zahlen."""
    return [(0, int(part), "") if part.isdigit() else (1, 0, part) for part in re.split(r"[.\-_+]", version) if part]


def load(path):
    with open(path, encoding="utf-8-sig") as handle:
        return yaml.safe_load(handle)


def latest_version(root, package_id):
    directory = os.path.join(root, manifest_dir(package_id))
    if not os.path.isdir(directory):
        return None
    # Unterordner ohne Installer-Manifest sind eigene Pakete (z. B. Mozilla.Firefox.ESR unter Mozilla.Firefox).
    versions = [name for name in os.listdir(directory) if os.path.isfile(os.path.join(directory, name, package_id + ".installer.yaml"))]
    return max(versions, key=version_key) if versions else None


def pick_installer(manifest):
    candidates = []
    for installer in manifest.get("Installers", []):
        kind = (installer.get("InstallerType") or manifest.get("InstallerType") or "").lower()
        architecture = (installer.get("Architecture") or "").lower()
        # 32-Bit-Setups nur als Ausweg, wenn der Hersteller nichts anderes anbietet (z. B. WinSCP).
        if kind not in PREFERRED_TYPES or architecture not in ("x64", "neutral", "x86"):
            continue
        scope = (installer.get("Scope") or manifest.get("Scope") or "").lower()
        locale = (installer.get("InstallerLocale") or "").lower()
        candidates.append((
            scope == "user",                      # Installation für alle Benutzer bevorzugen
            ("x64", "neutral", "x86").index(architecture),
            locale not in ("", "en-us", "de-de"),  # sprachneutrale bzw. englische/deutsche Setups zuerst
            PREFERRED_TYPES.index(kind),
            len(candidates),
            installer, kind))
    if not candidates:
        return None, None
    best = min(candidates, key=lambda c: c[:5])
    return best[5], best[6]


def silent_arguments(manifest, installer, kind):
    switches = {**(manifest.get("InstallerSwitches") or {}), **(installer.get("InstallerSwitches") or {})}
    custom = (switches.get("Custom") or "").strip()
    if kind in ("wix", "msi"):
        # Der Runner hängt /qn /norestart selbst an; nur zusätzliche Eigenschaften übernehmen.
        return custom
    silent = (switches.get("Silent") or DEFAULT_SILENT.get(kind) or "").strip()
    if not silent:
        return None
    return (silent + " " + custom).strip()


def build_entry(root, package_id):
    version = latest_version(root, package_id)
    if version is None:
        return None, "kein Manifest gefunden"
    directory = os.path.join(root, manifest_dir(package_id), version)
    manifest = load(os.path.join(directory, package_id + ".installer.yaml"))
    installer, kind = pick_installer(manifest)
    if installer is None:
        return None, "kein unbeaufsichtigt installierbares Setup (nur MSIX, Store, ZIP oder ARM)"
    arguments = silent_arguments(manifest, installer, kind)
    if arguments is None:
        return None, "keine Silent-Parameter bekannt"

    version_manifest = load(os.path.join(directory, package_id + ".yaml"))
    locale_path = os.path.join(directory, f"{package_id}.locale.{version_manifest.get('DefaultLocale', 'en-US')}.yaml")
    locale = load(locale_path) if os.path.isfile(locale_path) else {}
    entries = installer.get("AppsAndFeaturesEntries") or manifest.get("AppsAndFeaturesEntries") or []
    name = locale.get("PackageName") or package_id
    extension = "msi" if kind in ("wix", "msi") else "exe"
    codes = sorted({0, 3010, 1641, *(installer.get("InstallerSuccessCodes") or manifest.get("InstallerSuccessCodes") or [])})
    return {
        "id": package_id,
        "name": name,
        "publisher": locale.get("Publisher"),
        "version": str(version),
        "description": locale.get("ShortDescription"),
        "homepage": locale.get("PackageUrl"),
        "license": locale.get("License"),
        "installerType": kind,
        "url": installer["InstallerUrl"],
        "sha256": installer["InstallerSha256"].lower(),
        "fileName": re.sub(r"[^A-Za-z0-9.+_-]", "_", f"{package_id}-{version}") + "." + extension,
        "arguments": arguments,
        "successCodes": codes,
        # Woran das installierte Programm im Inventar zu erkennen ist ("Programme und Features").
        # Ohne Zusätze in Klammern, damit z. B. "Mozilla Firefox" auch "Mozilla Firefox (x64 de)" trifft.
        "match": re.sub(r"\s*\([^)]*\)\s*$", "", (entries[0].get("DisplayName") if entries else None) or name).strip() or name,
    }, None


def main():
    output = sys.argv[1]
    work = sys.argv[2] if len(sys.argv) > 2 else "winget-pkgs"
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "apps.txt"), encoding="utf-8") as handle:
        ids = [line.strip() for line in handle if line.strip() and not line.startswith("#")]

    if not os.path.isdir(os.path.join(work, ".git")):
        # tree:0 holt Verzeichnisse und Dateien erst bei Bedarf: wenige MB statt des gesamten Repositories.
        run("git", "clone", "--quiet", "--filter=tree:0", "--sparse", "--depth", "1", WINGET_REPO, work)
    run("git", "sparse-checkout", "set", "--no-cone", *["/" + manifest_dir(i) + "/" for i in ids], cwd=work)

    packages, skipped = [], []
    for package_id in ids:
        try:
            entry, reason = build_entry(work, package_id)
        except (OSError, KeyError, yaml.YAMLError) as error:
            entry, reason = None, f"Manifest nicht lesbar: {error}"
        if entry:
            packages.append(entry)
            print(f"  {package_id} {entry['version']} ({entry['installerType']})")
        else:
            skipped.append(package_id)
            print(f"  übersprungen: {package_id} – {reason}")

    packages.sort(key=lambda p: p["name"].lower())
    catalog = {
        "generated": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": WINGET_REPO,
        "packages": packages,
    }
    os.makedirs(os.path.dirname(os.path.abspath(output)), exist_ok=True)
    with open(output, "w", encoding="utf-8") as handle:
        json.dump(catalog, handle, ensure_ascii=False, indent=1)
    print(f"{len(packages)} Pakete geschrieben, {len(skipped)} übersprungen.")
    # Ein fast leerer Katalog deutet auf ein Problem beim Abruf hin; dann lieber den alten Stand behalten.
    return 0 if len(packages) >= len(ids) // 2 else 1


if __name__ == "__main__":
    sys.exit(main())
