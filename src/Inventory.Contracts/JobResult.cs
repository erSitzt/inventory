using System;
using System.Collections.Generic;

namespace Inventory.Contracts
{
    public enum StepStatus
    {
        Succeeded,
        Failed,
        Skipped,
        /// <summary>Wird nach dem Neustart des Rechners in einem Folgejob ausgeführt.</summary>
        Deferred = 3,
    }

    public enum ProgressKind
    {
        StepStarted,
        Output,
        StepFinished,
    }

    /// <summary>Eine Zeile in <see cref="JobFiles.Progress"/>; der Server liest sie während der Ausführung mit.</summary>
    public sealed class ProgressEvent
    {
        public ProgressKind Kind { get; set; }
        public int Step { get; set; }
        public string? Text { get; set; }
        public StepStatus? Status { get; set; }
        public int? ExitCode { get; set; }
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class JobResult
    {
        public string JobId { get; set; } = "";
        public bool Success { get; set; }
        /// <summary>Fehler außerhalb eines Schritts (Manifest unlesbar, Transport fehlgeschlagen …).</summary>
        public string? Error { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public List<StepResult> Steps { get; set; } = new List<StepResult>();
        /// <summary>Index des ersten Schritts, der nach einem erfolgreichen Neustart-Schritt noch aussteht; null = alles erledigt.</summary>
        public int? ResumeFromStep { get; set; }
        /// <summary>Startzeit des Betriebssystems laut Rechner; damit erkennt der Server, ob der Neustart stattgefunden hat.</summary>
        public DateTime? BootTimeUtc { get; set; }
        /// <summary>true, wenn der Job wegen <see cref="JobManifest.RequireBootAfterUtc"/> nicht ausgeführt wurde: der Rechner ist noch nicht neu gestartet.</summary>
        public bool NotRebootedYet { get; set; }
        /// <summary>true, wenn <see cref="JobManifest.Detection"/> schon vor dem ersten Schritt zutraf und deshalb nichts ausgeführt wurde.</summary>
        public bool AlreadyInstalled { get; set; }
        /// <summary>Gefüllt, wenn der Job einen Inventar-Scan enthielt.</summary>
        public InventoryData? Inventory { get; set; }

        public static JobResult Failed(string jobId, string error) =>
            new JobResult { JobId = jobId, Success = false, Error = error, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow };
    }

    public sealed class StepResult
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public StepStatus Status { get; set; }
        public int? ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string? Error { get; set; }
        public double DurationSeconds { get; set; }
    }
}
