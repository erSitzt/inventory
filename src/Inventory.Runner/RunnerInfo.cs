using System;
using System.IO;
using System.Reflection;
using Inventory.Contracts;

namespace Inventory.Runner
{
    /// <summary>Wo und womit der Runner läuft.</summary>
    internal static class RunnerInfo
    {
        /// <summary>
        /// Der Ordner der Runner-Dateien. Nicht über Assembly.Location: die eigenständige Variante ist eine einzige Datei,
        /// dort ist der Pfad der Assembly leer.
        /// </summary>
        public static string Directory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

#if NETFRAMEWORK
        public const string Runtime = RunnerRuntimes.Framework;
#else
        public const string Runtime = RunnerRuntimes.SelfContained;
#endif
    }
}
