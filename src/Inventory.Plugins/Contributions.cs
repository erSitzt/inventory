namespace Inventory.Plugins;

/// <summary>Eintrag im Menü des Servers; die Seite selbst ist eine Komponente mit <c>@page</c> in der Plugin-Assembly.</summary>
/// <param name="Href">Relativ, ohne führenden Schrägstrich, z. B. "contoso/tickets".</param>
/// <param name="Icon">SVG-Pfad eines MudBlazor-Symbols (z. B. <c>Icons.Material.Filled.Extension</c>); leer = Standardsymbol.</param>
public sealed record PluginNavItem(string Title, string Href, string? Icon = null, PluginRole MinRole = PluginRole.Viewer);

/// <summary>Zusätzlicher Reiter auf der Seite eines Rechners.</summary>
/// <param name="Component">Razor-Komponente mit einem Parameter <c>[Parameter] public int ComputerId { get; set; }</c>.</param>
public sealed record PluginComputerTab(string Title, Type Component, PluginRole MinRole = PluginRole.Viewer);

/// <summary>Abschnitt auf der Einstellungsseite (nur Administratoren).</summary>
public sealed record PluginSettingsSection(string Title, Type Component);

/// <summary>Werkzeugklasse für den MCP-Server (Attribute wie im SDK ModelContextProtocol); Rollen über <see cref="PluginPolicies"/>.</summary>
public sealed record PluginMcpTools(Type ToolType);

/// <summary>Aktion für ausgewählte Rechner: im Menü der Rechnerliste und auf der Rechnerseite, über API und MCP.</summary>
public interface IComputerAction
{
    /// <summary>Eindeutig innerhalb des Servers, z. B. "contoso.tickets.create".</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>SVG-Pfad eines MudBlazor-Symbols; null = Standardsymbol.</summary>
    string? Icon => null;

    /// <summary>Wer die Aktion ausführen darf; Operatoren nur für Rechner in ihrem Bereich.</summary>
    PluginRole MinRole => PluginRole.Operator;

    /// <summary>Rückfrage vor dem Ausführen; null = keine.</summary>
    string? Confirm => null;

    Task<PluginActionResult> RunAsync(IReadOnlyList<int> computerIds, PluginActionContext context, CancellationToken ct);
}

/// <param name="User">Wer die Aktion ausgelöst hat, wie er im Protokoll steht.</param>
public sealed record PluginActionContext(string User);

public sealed record PluginActionResult(bool Success, string Message)
{
    public static PluginActionResult Ok(string message) => new(true, message);

    public static PluginActionResult Failed(string message) => new(false, message);
}

/// <summary>Regelmäßige Arbeit, etwa ein Abgleich mit einem anderen System. Läuft nie parallel zu sich selbst.</summary>
public interface IPeriodicJob
{
    /// <summary>Eindeutig innerhalb des Plugins.</summary>
    string Name { get; }

    TimeSpan Interval { get; }

    Task RunAsync(CancellationToken ct);
}
