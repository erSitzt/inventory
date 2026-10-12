using System.Security.Cryptography;
using Inventory.Contracts;

namespace Inventory.Remoting;

/// <summary>Die Runner-Dateien, die der Server auf Zielrechner verteilt.</summary>
public sealed class RunnerPackage
{
    public RunnerPackage(string directory)
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
    }

    public string Directory { get; }
    public IReadOnlyList<string> Files { get; }

    /// <summary>Inhalts-Hash: benennt den Ablageordner auf dem Ziel, sodass ein geänderter Runner nie mit einem alten verwechselt wird.</summary>
    public string Hash { get; }

    /// <summary>SHA-256 der Runner-Datei selbst: der Wert, unter dem Endpoint-Schutz die Datei führt (für Freigaben per Datei-Hash).</summary>
    public string ExeSha256 { get; }
}
