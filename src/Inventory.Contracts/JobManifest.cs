using System;
using System.Collections.Generic;

namespace Inventory.Contracts
{
    public enum StepType
    {
        Command,
        PowerShell,
        /// <summary>Hard- und Software des Zielrechners erfassen; das Ergebnis steht in <see cref="JobResult.Inventory"/>.</summary>
        InventoryScan,
    }

    /// <summary>Auftrag an den Runner: liegt als <see cref="JobFiles.Manifest"/> im Job-Verzeichnis auf dem Zielrechner.</summary>
    public sealed class JobManifest
    {
        public string JobId { get; set; } = Guid.NewGuid().ToString("N");
        public List<JobStep> Steps { get; set; } = new List<JobStep>();
    }

    public sealed class JobStep
    {
        public string Name { get; set; } = "";
        public StepType Type { get; set; }
        /// <summary>Befehlszeile (Command) bzw. Skripttext (PowerShell).</summary>
        public string Script { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 600;
        public List<int> SuccessCodes { get; set; } = new List<int> { 0 };
        /// <summary>Bei Fehler mit dem nächsten Schritt weitermachen statt abzubrechen.</summary>
        public bool ContinueOnError { get; set; }
    }

    public static class JobFiles
    {
        public const string Manifest = "job.json";
        public const string Progress = "progress.jsonl";
        public const string Result = "result.json";
        public const string RunnerExe = "InventoryRunner.exe";
        /// <summary>Unterhalb von ADMIN$ (%SystemRoot%) auf dem Zielrechner.</summary>
        public const string RemoteRoot = "Inventory";
        public const string RunnerFolder = "Runner";
        public const string JobsFolder = "Jobs";
    }
}
