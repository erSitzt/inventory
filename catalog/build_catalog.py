#!/usr/bin/env python3
"""Erzeugt catalog.json für die Paketbibliothek aus den Manifesten des winget-Repositories.

Aufruf:  python3 catalog/build_catalog.py <ausgabe.json> [arbeitsordner] [--index <winget.json>]

Lädt nur die Manifest-Ordner der gewünschten Pakete (partieller Git-Klon), wählt je Paket die höchste Version
und daraus ein unbeaufsichtigt installierbares Setup (64 Bit bevorzugt). Braucht git und PyYAML.

Mit --index entsteht zusätzlich der Suchindex über das gesamte winget-Repository: alle Pakete mit unbeaufsichtigt
installierbarem Setup, ungeprüft und in gekürzter Form. Dafür wird das Repository vollständig (ohne Historie) geholt.
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


YAML_LOADER = getattr(yaml, "CSafeLoader", yaml.SafeLoader)


def load(path):
    with open(path, encoding="utf-8-sig") as handle:
        return yaml.load(handle, Loader=YAML_LOADER)


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


def shorten(text, limit=600):
    text = re.sub(r"\s+", " ", text or "").strip()
    return (text[:limit].rsplit(" ", 1)[0] + " …") if len(text) > limit else (text or None)


def exit_code(value):
    """Manifeste nennen Rückgabewerte teils vorzeichenlos (0xC0000000 und höher); Windows-Prozesse liefern 32 Bit mit Vorzeichen."""
    value = int(value)
    return value - (1 << 32) if value >= 1 << 31 else value


def build_entry(root, package_id, directory=None):
    if directory is None:
        version = latest_version(root, package_id)
        if version is None:
            return None, "kein Manifest gefunden"
        directory = os.path.join(root, manifest_dir(package_id), version)
    version = os.path.basename(directory)
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
    release_date = installer.get("ReleaseDate") or manifest.get("ReleaseDate")
    codes = sorted({0, 3010, 1641, *map(exit_code, installer.get("InstallerSuccessCodes") or manifest.get("InstallerSuccessCodes") or [])})
    return {
        "id": package_id,
        "name": name,
        "publisher": locale.get("Publisher"),
        "version": str(version),
        "description": locale.get("ShortDescription"),
        "homepage": locale.get("PackageUrl"),
        "license": locale.get("License"),
        "licenseUrl": locale.get("LicenseUrl"),
        # Ausführliche Angaben nur, soweit das Manifest sie nennt; die Oberfläche lässt Fehlendes weg.
        "details": shorten(locale.get("Description")),
        "tags": [str(tag) for tag in (locale.get("Tags") or [])][:10],
        "moniker": locale.get("Moniker"),
        "supportUrl": locale.get("PublisherSupportUrl"),
        "releaseNotesUrl": locale.get("ReleaseNotesUrl"),
        "releaseDate": str(release_date) if release_date else None,
        "installerType": kind,
        "architecture": (installer.get("Architecture") or "").lower() or None,
        "scope": (installer.get("Scope") or manifest.get("Scope") or "").lower() or None,
        "minimumOsVersion": installer.get("MinimumOSVersion") or manifest.get("MinimumOSVersion"),
        "url": installer["InstallerUrl"],
        "sha256": installer["InstallerSha256"].lower(),
        "fileName": re.sub(r"[^A-Za-z0-9.+_-]", "_", f"{package_id}-{version}") + "." + extension,
        "arguments": arguments,
        "successCodes": codes,
        # Woran das installierte Programm im Inventar zu erkennen ist ("Programme und Features").
        # Ohne Zusätze in Klammern, damit z. B. "Mozilla Firefox" auch "Mozilla Firefox (x64 de)" trifft.
        "match": re.sub(r"\s*\([^)]*\)\s*$", "", (entries[0].get("DisplayName") if entries else None) or name).strip() or name,
    }, None


def all_packages(root):
    """Alle Pakete des Repositories: ID -> Ordner der höchsten Version."""
    suffix = ".installer.yaml"
    found = {}
    for directory, _, files in os.walk(os.path.join(root, "manifests")):
        for name in files:
            if name.endswith(suffix):
                found.setdefault(name[:-len(suffix)], []).append(directory)
    return {package_id: max(directories, key=lambda d: version_key(os.path.basename(d))) for package_id, directories in found.items()}


def build_index(root):
    """Suchindex über alle Pakete; gekürzt, weil er mehrere tausend Einträge enthält."""
    entries = []
    for package_id, directory in sorted(all_packages(root).items()):
        try:
            entry, _ = build_entry(root, package_id, directory)
        except Exception:  # ein einzelnes fehlerhaftes Manifest soll den Index nicht verhindern
            entry = None
        # Ohne http(s)-Adresse und Prüfsumme lässt sich nichts sicher laden.
        if not entry or not re.match(r"https?://", str(entry["url"]), re.I) or not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"]):
            continue
        entry["details"] = None
        entry["tags"] = entry["tags"][:6]
        entry["description"] = shorten(entry["description"], 200)
        entries.append({key: value for key, value in entry.items() if value not in (None, [])})
    return entries


def write_json(path, packages, indent=1):
    # YAML liest Angaben wie "1.10" oder "2024" als Zahl; der Server erwartet überall Text.
    for package in packages:
        for key, value in package.items():
            if key not in ("tags", "successCodes") and value is not None and not isinstance(value, str):
                package[key] = str(value)
    catalog = {
        "generated": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": WINGET_REPO,
        "packages": packages,
    }
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(catalog, handle, ensure_ascii=False, indent=indent)


def main():
    arguments = sys.argv[1:]
    index_output = None
    if "--index" in arguments:
        position = arguments.index("--index")
        index_output = arguments[position + 1]
        del arguments[position:position + 2]
    output = arguments[0]
    work = arguments[1] if len(arguments) > 1 else "winget-pkgs"
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "apps.txt"), encoding="utf-8") as handle:
        ids = [line.strip() for line in handle if line.strip() and not line.startswith("#")]

    if index_output:
        if not os.path.isdir(os.path.join(work, ".git")):
            run("git", "clone", "--quiet", "--depth", "1", WINGET_REPO, work)
    else:
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
    write_json(output, packages)
    print(f"{len(packages)} Pakete geschrieben, {len(skipped)} übersprungen.")
    # Ein fast leerer Katalog deutet auf ein Problem beim Abruf hin; dann lieber den alten Stand behalten.
    if len(packages) < len(ids) // 2:
        return 1

    if index_output:
        index = build_index(work)
        write_json(index_output, index, indent=None)
        print(f"Suchindex: {len(index)} Pakete.")
        if len(index) < 1000:
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
