<#
.SYNOPSIS
    Signiert Dateien mit dem Code-Signing-Zertifikat aus den Umgebungsvariablen SIGNING_CERT_PFX (Base64) und SIGNING_CERT_PASSWORD.
#>
param([Parameter(Mandatory)][string[]]$Files)
$ErrorActionPreference = 'Stop'

$pfx = Join-Path $env:RUNNER_TEMP 'signing.pfx'
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGNING_CERT_PFX))
try {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe nicht gefunden' }
    foreach ($file in $Files) {
        & $signtool.FullName sign /f $pfx /p $env:SIGNING_CERT_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $file
        if ($LASTEXITCODE -ne 0) { throw "Signieren von $file fehlgeschlagen" }
    }
}
finally {
    Remove-Item $pfx -Force
}
