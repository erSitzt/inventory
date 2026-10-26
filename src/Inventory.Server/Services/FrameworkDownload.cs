using Inventory.Core;
using Inventory.Core.Abstractions;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Services;

/// <summary>
/// Lädt den Offline-Installer von .NET Framework 4.8 einmal von Microsoft und legt ihn im Datenordner ab. Ohne
/// Internetzugang des Servers kann ein Administrator die Datei selbst dorthin kopieren. Genutzt wird sie nur mit gültiger
/// Signatur von Microsoft.
/// </summary>
public sealed class FrameworkDownload(string directory, HttpClient http, IStringLocalizer<SharedResource> L) : IFrameworkSource
{
    /// <summary>Offizieller Verweis von Microsoft auf den Offline-Installer (alle Sprachen der Oberfläche, x86 und x64).</summary>
    public const string Url = "https://go.microsoft.com/fwlink/?linkid=2088631";
    public const string FileName = "ndp48-x86-x64-allos-enu.exe";
    private const string Publisher = "Microsoft Corporation";

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Path => System.IO.Path.Combine(directory, FileName);

    public async Task<string> GetInstallerAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(Path))
                await DownloadAsync(ct);
            if (Verify(Path) is { } problem)
                throw new InvalidOperationException(L["The .NET Framework installer {0} is not usable: {1}", Path, problem]);
            return Path;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var part = Path + ".part";
        try
        {
            using (var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(part);
                await response.Content.CopyToAsync(file, ct);
            }
            // Erst die geprüfte Datei bekommt ihren Namen; eine abgebrochene oder fremde bleibt nie liegen.
            if (Verify(part) is { } problem)
                throw new InvalidOperationException(L["The downloaded .NET Framework installer was rejected: {0}", problem]);
            File.Move(part, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(L["The .NET Framework installer could not be downloaded ({0}). Without internet access, copy {1} from Microsoft to {2}.", ex.Message, FileName, directory], ex);
        }
        finally
        {
            File.Delete(part);
        }
    }

    private static string? Verify(string path) => OperatingSystem.IsWindows()
        ? SelfUpdate.VerifySignature(path, Publisher, [])
        : "Signature verification is only available on Windows.";
}
