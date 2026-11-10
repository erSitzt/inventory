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
    /// <param name="macDirectory">
    /// Der Mac-Agent, je Laufzeit (<see cref="RunnerRuntimes.MacArm64"/>, <see cref="RunnerRuntimes.MacX64"/>) ein Unterordner mit
    /// <see cref="AgentProtocol.MacAgentFile"/>. Fehlt er, gibt es keine Mac-Agenten.
    /// </param>
    public RunnerPackage(string directory, string? selfContainedDirectory = null, string? macDirectory = null)
        : this(directory, JobFiles.RunnerExe)
    {
        if (selfContainedDirectory is not null && File.Exists(Path.Combine(selfContainedDirectory, JobFiles.RunnerExe)))
            SelfContained = new RunnerPackage(selfContainedDirectory, JobFiles.RunnerExe) { Runtime = RunnerRuntimes.SelfContained };
        foreach (var runtime in new[] { RunnerRuntimes.MacArm64, RunnerRuntimes.MacX64 })
        {
            var folder = macDirectory is null ? null : Path.Combine(macDirectory, runtime);
            if (folder is not null && File.Exists(Path.Combine(folder, AgentProtocol.MacAgentFile)))
                _mac[runtime] = new RunnerPackage(folder, AgentProtocol.MacAgentFile) { Runtime = runtime };
        }
    }

    private RunnerPackage(string directory, string exe)
    {
        Directory = directory;
        if (!File.Exists(Path.Combine(directory, exe)))
            throw new FileNotFoundException($"Runner not found: {Path.Combine(directory, exe)}");

        Files = System.IO.Directory.GetFiles(directory).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files)
            sha.AppendData(File.ReadAllBytes(file));
        ExeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, exe)))).ToLowerInvariant();
        Hash = Convert.ToHexString(sha.GetHashAndReset())[..16].ToLowerInvariant();
    }

    private readonly Dictionary<string, RunnerPackage> _mac = new(StringComparer.Ordinal);

    /// <summary><see cref="RunnerRuntimes"/>: womit diese Runner-Dateien laufen.</summary>
    public string Runtime { get; private init; } = RunnerRuntimes.Framework;

    /// <summary>Die eigenständige .NET-10-Variante; null, wenn der Server sie nicht mitbringt.</summary>
    public RunnerPackage? SelfContained { get; }

    /// <summary>Die SHA-256-Werte aller Runner-Dateien, die der Server verteilt: zuerst der kleine Runner.</summary>
    public IReadOnlyList<string> AllExeSha256 => SelfContained is null ? [ExeSha256] : [ExeSha256, SelfContained.ExeSha256];

    /// <summary>
    /// Die Variante für die angegebene Laufzeit; leer heißt .NET Framework 4.8 (ältere Agenten). null, wenn der Server die
    /// verlangte Variante nicht hat oder die Laufzeit nicht kennt: eine andere liefe dort nicht.
    /// </summary>
    public RunnerPackage? For(string? runtime) => runtime switch
    {
        null or "" or RunnerRuntimes.Framework => this,
        RunnerRuntimes.SelfContained => SelfContained,
        _ => _mac.GetValueOrDefault(runtime),
    };

    public string Directory { get; }
    public IReadOnlyList<string> Files { get; }

    /// <summary>Inhalts-Hash: benennt den Ablageordner auf dem Ziel, sodass ein geänderter Runner nie mit einem alten verwechselt wird.</summary>
    public string Hash { get; }

    /// <summary>SHA-256 der Runner-Datei selbst: der Wert, unter dem Endpoint-Schutz die Datei führt (für Freigaben per Datei-Hash).</summary>
    public string ExeSha256 { get; }
}
