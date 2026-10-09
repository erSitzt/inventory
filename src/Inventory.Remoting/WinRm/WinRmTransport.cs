using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Inventory.Contracts;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;

using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Remoting.WinRm;

/// <summary>
/// Agentenloser Zugriff über PowerShell-Remoting (WinRM, Port 5985/5986). Die Sitzung baut eine lokal gestartete
/// Windows PowerShell auf; dadurch braucht der Server kein eingebettetes PowerShell-SDK. Der Runner läuft im
/// Konsolenmodus mit dem angegebenen Konto (nicht als Lokales System).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinRmTransport(RunnerPackage runner, IStringLocalizer<SharedResource>? localizer = null) : IRemoteTransport
{    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;


    private static readonly Lazy<string> EncodedScript = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Inventory.Remoting.WinRm.RemoteJob.ps1")
                           ?? throw new InvalidOperationException("Eingebettetes Skript RemoteJob.ps1 fehlt");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // -EncodedCommand erwartet UTF-16; so spielen Ausführungsrichtlinie und Zeichensatz der Konsole keine Rolle.
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
    });

    public TransportKind Kind => TransportKind.WinRm;

    public async Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct)
    {
        var config = JsonSerializer.Serialize(new
        {
            host = target.Host,
            user = target.Credential?.UserName,
            password = target.Credential?.Password,
            jobId = job.JobId,
            runnerHash = runner.Hash,
            runnerFiles = runner.Files,
            files = files.Select(f => new { local = f.LocalPath, relative = f.RelativePath }),
            manifest = JobJson.Serialize(job),
        });

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", EncodedScript.Value },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.Start();
        // Beendet die lokale PowerShell und damit die Remote-Sitzung samt Runner.
        await using var cancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* bereits beendet */ }
        });

        // Zugangsdaten und Job gehen über stdin, nie über die Befehlszeile (die wäre in der Prozessliste sichtbar).
        await process.StandardInput.WriteAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(config)));
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        JobResult? result = null;
        string? error = null;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            switch (Parse(line))
            {
                case (MessageKind.Progress, var json) when JobJson.Deserialize<ProgressEvent>(json) is { } e:
                    progress?.Report(e);
                    break;
                case (MessageKind.Result, var base64):
                    result = JobJson.Deserialize<JobResult>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)).TrimStart('﻿'));
                    break;
                case (MessageKind.Error, var message):
                    error = message;
                    break;
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (result is not null)
            return result;
        var details = Explain(error, L) ?? ErrorText(await stderr);
        throw new RemoteException(L["WinRM access to {0} failed: {1}", target.Host, details.Length > 0 ? details : L["PowerShell exited with code {0}", process.ExitCode].Value]);
    }

    internal static string? Explain(string? error, IStringLocalizer<SharedResource>? localizer = null)
    {
        var L = localizer ?? PassthroughLocalizer.Instance;
        return error switch
        {
            null => null,
            _ when error.Contains("CONSTRAINED_LOCAL") =>
                L["PowerShell on the server runs in constrained language mode (AppLocker/WDAC policy). The WinRM transport needs full language mode for the server service's account."],
            _ when error.Contains("CONSTRAINED_REMOTE") =>
                L["PowerShell on the target computer runs in constrained language mode for this account (AppLocker/WDAC policy). The WinRM transport cannot be used there; please use SMB + service."],
            _ => error,
        };
    }

    /// <summary>Nicht interaktiv gestartet liefert PowerShell Fehler als CLIXML; daraus nur den lesbaren Text holen.</summary>
    internal static string ErrorText(string stderr)
    {
        if (!stderr.Contains("<S S=\"Error\">"))
            return stderr.Trim();
        var text = string.Concat(System.Text.RegularExpressions.Regex.Matches(stderr, "<S S=\"Error\">(.*?)</S>").Select(m => m.Groups[1].Value));
        return System.Net.WebUtility.HtmlDecode(text.Replace("_x000D__x000A_", " ")).Trim();
    }

    internal enum MessageKind { Other, Progress, Result, Error }

    /// <summary>Zerlegt eine Ausgabezeile des Skripts in Art und Inhalt.</summary>
    internal static (MessageKind Kind, string Payload) Parse(string line) => line switch
    {
        ['P', ':', .. var rest] => (MessageKind.Progress, rest),
        ['R', ':', .. var rest] => (MessageKind.Result, rest),
        ['E', ':', .. var rest] => (MessageKind.Error, rest),
        _ => (MessageKind.Other, line),
    };
}
