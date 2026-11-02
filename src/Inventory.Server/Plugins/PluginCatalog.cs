using System.Reflection;
using Inventory.Plugins;

namespace Inventory.Server.Plugins;

public enum PluginState
{
    /// <summary>Gefunden, aber nicht eingeschaltet.</summary>
    Disabled,
    /// <summary>Eingeschaltet, doch die Dateien haben sich seither geändert: bleibt aus, bis ein Administrator erneut bestätigt.</summary>
    Changed,
    Loaded,
    Failed,
    Incompatible,
}

/// <summary>Zustand eines regelmäßigen Auftrags, für die Plugin-Seite.</summary>
public sealed class PluginJobStatus
{
    public required string Name { get; init; }
    public TimeSpan Interval { get; init; }
    public DateTime? LastRunUtc { get; set; }
    public string? LastError { get; set; }
    public bool Running { get; set; }
}

/// <summary>Ein Plugin-Ordner und was beim Start daraus geworden ist.</summary>
public sealed class PluginInfo
{
    /// <summary>Name des Ordners unter plugins und zugleich der Haupt-DLL.</summary>
    public required string Folder { get; init; }
    public required string MainFile { get; init; }
    /// <summary>Prüfsumme über alle DLLs des Ordners; ändert sie sich, wird das Plugin nicht mehr geladen.</summary>
    public required string Sha256 { get; init; }
    public string? FileVersion { get; init; }
    /// <summary>Unterzeichner der Haupt-DLL (Authenticode), ohne Prüfung der Vertrauenskette.</summary>
    public string? Publisher { get; init; }
    public PluginState State { get; set; }
    public string? Error { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ApiVersion { get; set; }
    public IInventoryPlugin? Plugin { get; set; }
    public Assembly? Assembly { get; set; }
    /// <summary>Kurzbeschreibung der Beiträge, z. B. "2 pages, 1 action".</summary>
    public List<string> Contributions { get; } = [];
    public List<PluginJobStatus> Jobs { get; } = [];

    public string DisplayName => Name ?? Folder;
}

/// <summary>Alle beim Start gefundenen Plugins; welche geladen sind und was sie beitragen.</summary>
public sealed class PluginCatalog(string directory, IReadOnlyList<PluginInfo> plugins)
{
    /// <summary>Ordner, in dem Plugins liegen (je Plugin ein Unterordner).</summary>
    public string Directory { get; } = directory;

    public IReadOnlyList<PluginInfo> Plugins { get; } = plugins;

    public IEnumerable<PluginInfo> Loaded => Plugins.Where(p => p.State == PluginState.Loaded);

    /// <summary>Für den Router: Seiten in Plugin-Assemblies.</summary>
    public Assembly[] Assemblies => [.. Loaded.Select(p => p.Assembly!)];

    /// <summary>Werkzeugklassen der Plugins für den MCP-Server.</summary>
    public List<Type> McpToolTypes { get; } = [];

    /// <summary>Das Plugin, aus dessen Assembly ein Typ stammt; null für Typen des Servers.</summary>
    public PluginInfo? Owner(Type type) => Loaded.FirstOrDefault(p => p.Assembly == type.Assembly);

    public static PluginCatalog Empty => new("", []);
}
