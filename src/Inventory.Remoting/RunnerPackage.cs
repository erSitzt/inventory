using System.Security.Cryptography;
using Inventory.Contracts;

namespace Inventory.Remoting;

/// <summary>Die Runner-Dateien, die der Server auf Zielrechner verteilt.</summary>
public sealed class RunnerPackage
{
    /// <param name="selfContainedDirectory">
    /// Der Runner eigenständig mit .NET 10 (eine Datei); nur für Rechner ohne .NET Framework 4.8. Fehlt der Ordner, gibt es
    /// keine Ausweichvariante.
    /// </param>
    public RunnerPackage(string directory, string? selfContainedDirectory = null)
    {
        Directory = directory;
        if (!File.Exists(Path.Combine(directory, JobFiles.RunnerExe)))
            throw new FileNotFoundException($"Runner not found: {Path.Combine(directory, JobFiles.RunnerExe)}");

        Files = System.IO.Directory.GetFiles(directory).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files)
            sha.AppendData(File.ReadAllBytes(file));
        ExeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, JobFiles.RunnerExe)))).ToLowerInvariant();
        Hash = Convert.ToHexString(sha.GetHashAndReset())[..16].ToLowerInvariant();
        if (selfContainedDirectory is not null && File.Exists(Path.Combine(selfContainedDirectory, JobFiles.RunnerExe)))
            SelfContained = new RunnerPackage(selfContainedDirectory) { Runtime = RunnerRuntimes.SelfContained };
    }

    /// <summary><see cref="RunnerRuntimes"/>: womit diese Runner-Dateien laufen.</summary>
    public string Runtime { get; private init; } = RunnerRuntimes.Framework;

    /// <summary>Die eigenständige .NET-10-Variante; null, wenn der Server sie nicht mitbringt.</summary>
    public RunnerPackage? SelfContained { get; }

    /// <summary>Die SHA-256-Werte aller Runner-Dateien, die der Server verteilt: zuerst der kleine Runner.</summary>
    public IReadOnlyList<string> AllExeSha256 => SelfContained is null ? [ExeSha256] : [ExeSha256, SelfContained.ExeSha256];

    /// <summary>Die Variante für die angegebene Laufzeit; unbekannt oder leer heißt .NET Framework 4.8 (ältere Agenten).</summary>
    public RunnerPackage For(string? runtime) => runtime == RunnerRuntimes.SelfContained && SelfContained is not null ? SelfContained : this;

    public string Directory { get; }
    public IReadOnlyList<string> Files { get; }

    /// <summary>Inhalts-Hash: benennt den Ablageordner auf dem Ziel, sodass ein geänderter Runner nie mit einem alten verwechselt wird.</summary>
    public string Hash { get; }

    /// <summary>SHA-256 der Runner-Datei selbst: der Wert, unter dem Endpoint-Schutz die Datei führt (für Freigaben per Datei-Hash).</summary>
    public string ExeSha256 { get; }
}
