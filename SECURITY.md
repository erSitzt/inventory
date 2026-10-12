# Sicherheit / Security

Inventory verwaltet Administrator-Zugangsdaten und führt Befehle mit hohen Rechten auf anderen Rechnern aus.
Schwachstellen bitte **nicht** als öffentliches Issue melden, sondern vertraulich über
[„Report a vulnerability“](https://github.com/erSitzt/inventory/security/advisories/new) auf GitHub.

Bitte angeben: betroffene Version, Schritte zum Nachstellen und die mögliche Auswirkung. Du bekommst eine
Rückmeldung, sobald die Meldung gesichtet ist; behobene Schwachstellen werden in den Release-Notizen genannt.

Please report vulnerabilities privately via GitHub's "Report a vulnerability" form linked above, not as a public issue.

## Hinweise zum Betrieb

- Zugriff auf Oberfläche und API nur für die konfigurierten Gruppen; die Anmeldung nie abschalten, wenn der Server
  im Netz erreichbar ist.
- HTTPS verwenden, sobald von anderen Rechnern zugegriffen wird oder Agenten im Einsatz sind.
- Releases sind mit einem selbstsignierten Zertifikat des Projekts signiert; die Selbstaktualisierung nimmt nur Setups
  an, die von einem Zertifikat der installierten Version stammen. Das belegt die Herkunft vom selben Schlüssel, nicht
  die Identität des Herausgebers. Die automatische Aktualisierung ist standardmäßig aus; siehe Abschnitt
  „Aktualisieren“ in der README.
- Das Datenverzeichnis (`C:\ProgramData\Inventory`) enthält verschlüsselte Zugangsdaten und die Schlüssel dazu und
  ist nur für SYSTEM und Administratoren zugänglich.
