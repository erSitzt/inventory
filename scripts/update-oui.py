#!/usr/bin/env python3
"""Erzeugt src/Inventory.Core/Resources/oui.txt.gz aus dem öffentlichen MA-L-Register der IEEE (MAC-Präfix -> Hersteller)."""
import csv, gzip, io, pathlib, urllib.request

URL = "https://standards-oui.ieee.org/oui/oui.csv"
TARGET = pathlib.Path(__file__).resolve().parent.parent / "src/Inventory.Core/Resources/oui.txt.gz"

request = urllib.request.Request(URL, headers={"User-Agent": "curl/8.5.0"})
with urllib.request.urlopen(request, timeout=120) as response:
    text = response.read().decode("utf-8")

rows = {}
for row in csv.DictReader(io.StringIO(text)):
    name = " ".join(row["Organization Name"].split())
    if row["Registry"] == "MA-L" and name:
        rows[row["Assignment"].upper()] = name

data = "".join(f"{prefix}\t{name}\n" for prefix, name in sorted(rows.items()))
TARGET.write_bytes(gzip.compress(data.encode("utf-8"), 9, mtime=0))
print(f"{len(rows)} prefixes -> {TARGET}")
