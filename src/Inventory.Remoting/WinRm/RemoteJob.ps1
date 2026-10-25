# Führt einen Job per PowerShell-Remoting aus. Wird vom Server mit Windows PowerShell 5.1 gestartet.
# Eingabe (stdin): Base64 eines UTF-8-JSON mit host, user, password, jobId, runnerHash, runnerFiles[], files[{local,relative}], manifest.
# Ausgabe (stdout), eine Meldung je Zeile:  P:<Fortschritt als JSON>   R:<result.json als Base64>   E:<Fehlertext>
# Das Skript braucht .NET-Typen. Erzwingt eine Richtlinie (AppLocker/WDAC) den eingeschränkten Sprachmodus,
# lieber gleich mit einem eindeutigen Code abbrechen; der Server übersetzt ihn in eine verständliche Meldung.
if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') { 'E:CONSTRAINED_LOCAL'; exit 1 }
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$session = $null
$jobDir = $null
try {
    $cfg = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd())) | ConvertFrom-Json

    $connect = @{ ComputerName = $cfg.host }
    if ($cfg.user) {
        $connect.Credential = New-Object System.Management.Automation.PSCredential($cfg.user, (ConvertTo-SecureString $cfg.password -AsPlainText -Force))
    }
    $session = New-PSSession @connect

    # Gleiche Ablage wie beim SMB-Transport: %SystemRoot%\Inventory, nur für SYSTEM und Administratoren zugänglich.
    $remote = Invoke-Command -Session $session -ArgumentList $cfg.runnerHash, $cfg.jobId, @($cfg.files | ForEach-Object { $_.relative }) -ScriptBlock {
        param($hash, $jobId, $relativeFiles)
        if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') { throw 'CONSTRAINED_REMOTE' }
        # Der Runner braucht .NET Framework 4.8 (Release 528040); sonst startet er nicht und liefert kein Ergebnis.
        $ndp = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
        $release = if ($null -eq $ndp) { -1 } elseif ($ndp.Release) { [int]$ndp.Release } else { 0 }
        if ($release -lt 528040) { throw "NETFX_MISSING:$release" }
        $root = Join-Path $env:SystemRoot 'Inventory'
        if (-not (Test-Path $root)) {
            New-Item -ItemType Directory -Path $root | Out-Null
            & icacls.exe $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        }
        $runner = Join-Path $root "Runner\$hash"
        $job = Join-Path $root "Jobs\$jobId"
        New-Item -ItemType Directory -Force -Path $runner, $job | Out-Null
        foreach ($relative in $relativeFiles) {
            New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $job "files\$relative") -Parent) | Out-Null
        }
        [pscustomobject]@{ Runner = $runner; Job = $job; HasRunner = (Test-Path (Join-Path $runner '.complete')) }
    }
    $jobDir = $remote.Job

    if (-not $remote.HasRunner) {
        foreach ($file in $cfg.runnerFiles) {
            Copy-Item -ToSession $session -Path $file -Destination (Join-Path $remote.Runner (Split-Path $file -Leaf)) -Force
        }
        Invoke-Command -Session $session -ArgumentList $remote.Runner, $cfg.runnerHash -ScriptBlock {
            param($runner, $hash) Set-Content -Path (Join-Path $runner '.complete') -Value $hash
        }
    }
    foreach ($file in $cfg.files) {
        Copy-Item -ToSession $session -Path $file.local -Destination (Join-Path $remote.Job "files\$($file.relative)") -Force
    }

    Invoke-Command -Session $session -ArgumentList $remote.Runner, $remote.Job, $cfg.manifest -ScriptBlock {
        param($runner, $job, $manifest)
        [IO.File]::WriteAllText((Join-Path $job 'job.json'), $manifest, (New-Object System.Text.UTF8Encoding $false))
        $process = Start-Process -FilePath (Join-Path $runner 'InventoryRunner.exe') -ArgumentList '--console', "`"$job`"" -PassThru -WindowStyle Hidden

        $progress = Join-Path $job 'progress.jsonl'
        $state = @{ Position = 0 }
        $pump = {
            if (-not (Test-Path $progress)) { return }
            $stream = [IO.File]::Open($progress, 'Open', 'Read', 'ReadWrite, Delete')
            try {
                if ($stream.Length -le $state.Position) { return }
                $stream.Position = $state.Position
                $buffer = New-Object byte[] ($stream.Length - $state.Position)
                [void]$stream.Read($buffer, 0, $buffer.Length)
                # Nur vollständige Zeilen weitergeben; eine angefangene kommt beim nächsten Durchlauf.
                $end = [Array]::LastIndexOf($buffer, [byte]10) + 1
                if ($end -gt 0) {
                    $state.Position += $end
                    [Text.Encoding]::UTF8.GetString($buffer, 0, $end).Split("`n", [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { "P:$($_.Trim())" }
                }
            } finally { $stream.Dispose() }
        }
        while (-not $process.HasExited) { & $pump; Start-Sleep -Milliseconds 500 }
        & $pump

        $result = Join-Path $job 'result.json'
        if (Test-Path $result) { 'R:' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($result)) }
        else { "E:Der Runner wurde beendet, ohne ein Ergebnis zu liefern (Exitcode $($process.ExitCode)). Mögliche Ursachen: .NET Framework 4.8 fehlt, Virenscanner oder AppLocker blockiert die Ausführung." }
    }
}
catch {
    'E:' + ($_.Exception.Message -replace '\s+', ' ')
}
finally {
    if ($session) {
        if ($jobDir) {
            try { Invoke-Command -Session $session -ArgumentList $jobDir -ScriptBlock { param($job) Remove-Item -Recurse -Force -Path $job -ErrorAction SilentlyContinue } } catch { }
        }
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }
}
