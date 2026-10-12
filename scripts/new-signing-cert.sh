#!/usr/bin/env bash
# Erzeugt ein selbstsigniertes Code-Signing-Zertifikat für die Releases.
#
# In den Ausgabeordner (außerhalb des Repositorys, Standard: ~/.inventory-signing) kommen
#   signing.pfx.b64   Base64 der PFX-Datei   -> GitHub-Secret SIGNING_CERT_PFX
#   signing.password  Passwort der PFX-Datei -> GitHub-Secret SIGNING_CERT_PASSWORD
# und das öffentliche Zertifikat nach installer/signing/<Fingerabdruck>.cer. Dieses wird eingecheckt: der Server
# spielt danach nur noch Updates ein, die mit einem der dort liegenden Zertifikate signiert sind.
#
# Aufruf: scripts/new-signing-cert.sh [--set-secrets]
#   --set-secrets  hinterlegt beide Werte mit der GitHub-CLI als Secrets des Repositorys
set -euo pipefail

subject="${SIGNING_SUBJECT:-/CN=Inventory Release Signing}"
years="${SIGNING_YEARS:-5}"
out="${SIGNING_OUT:-$HOME/.inventory-signing}"
public="${SIGNING_PUBLIC:-$(cd "$(dirname "$0")/.." && pwd)/installer/signing}"

umask 077
mkdir -p "$out" "$public"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days "$((years * 365))" -subj "$subject" \
    -addext 'basicConstraints=critical,CA:FALSE' -addext 'keyUsage=critical,digitalSignature' \
    -addext 'extendedKeyUsage=critical,codeSigning' \
    -keyout "$work/key.pem" -out "$work/cert.pem" 2>/dev/null

password="$(openssl rand -base64 24)"
# Ältere Verfahren in der PFX-Datei: signtool und Windows lesen sie auf jeder Version.
legacy=()
if openssl pkcs12 -help 2>&1 | grep -q -- '-legacy'; then legacy=(-legacy); fi
openssl pkcs12 -export "${legacy[@]}" -inkey "$work/key.pem" -in "$work/cert.pem" -name 'Inventory Release Signing' \
    -passout "pass:$password" -out "$work/signing.pfx"

base64 -w0 "$work/signing.pfx" > "$out/signing.pfx.b64"
printf '%s' "$password" > "$out/signing.password"
thumbprint="$(openssl x509 -in "$work/cert.pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f')"
openssl x509 -in "$work/cert.pem" -outform DER -out "$public/$thumbprint.cer"
chmod 644 "$public/$thumbprint.cer"

echo "Zertifikat:    $(openssl x509 -in "$work/cert.pem" -noout -subject | sed 's/^subject=//'), gültig bis $(openssl x509 -in "$work/cert.pem" -noout -enddate | cut -d= -f2)"
echo "Fingerabdruck: $thumbprint"
echo "Öffentlich:    $public/$thumbprint.cer   (einchecken)"
echo "Geheim:        $out   (sicher aufbewahren; wer die Datei hat, kann Releases signieren)"

if [[ "${1:-}" == "--set-secrets" ]]; then
    gh secret set SIGNING_CERT_PFX < "$out/signing.pfx.b64"
    gh secret set SIGNING_CERT_PASSWORD < "$out/signing.password"
    echo "Secrets SIGNING_CERT_PFX und SIGNING_CERT_PASSWORD sind hinterlegt."
else
    echo "Secrets hinterlegen:"
    echo "  gh secret set SIGNING_CERT_PFX < $out/signing.pfx.b64"
    echo "  gh secret set SIGNING_CERT_PASSWORD < $out/signing.password"
fi
