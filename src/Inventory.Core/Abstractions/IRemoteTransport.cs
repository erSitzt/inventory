using System.Net;
using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core.Abstractions;

/// <param name="Credential">null = mit der Identität des Server-Prozesses zugreifen.</param>
public sealed record RemoteTarget(string Host, NetworkCredential? Credential);

/// <summary>Datei, die mit dem Job auf den Zielrechner kopiert wird.</summary>
/// <param name="RelativePath">Pfad unterhalb des Ordners <see cref="JobFiles.FilesFolder"/> im Job-Verzeichnis.</param>
public sealed record JobFile(string LocalPath, string RelativePath);

/// <summary>Fehler beim Zustellen oder Starten eines Jobs (im Gegensatz zu fehlgeschlagenen Schritten).</summary>
public sealed class RemoteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Bringt einen Job auf den Zielrechner, lässt ihn dort vom Runner ausführen und holt das Ergebnis.</summary>
public interface IRemoteTransport
{
    TransportKind Kind { get; }

    /// <exception cref="RemoteException">Der Job konnte nicht zugestellt oder gestartet werden.</exception>
    Task<JobResult> RunAsync(RemoteTarget target, JobManifest job, IReadOnlyList<JobFile> files, IProgress<ProgressEvent>? progress, CancellationToken ct);
}
