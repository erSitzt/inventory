namespace Inventory.Core.Abstractions;

public enum CustomFieldKind
{
    Text,
    Number,
    /// <summary>Gespeichert als yyyy-MM-dd.</summary>
    Date,
    /// <summary>Gespeichert als true/false.</summary>
    Bool,
}

/// <param name="Key">"&lt;Plugin-Id&gt;.&lt;Name&gt;".</param>
/// <param name="Source">Name des Plugins: Kategorie im Änderungsverlauf und Gruppe in der Anzeige.</param>
public sealed record CustomFieldInfo(string Key, string Title, CustomFieldKind Kind, string Source);

/// <summary>PowerShell-Skript, das auf dem Rechner läuft und ein JSON-Objekt { Feld: Wert } ausgibt.</summary>
/// <param name="Id">"&lt;Plugin-Id&gt;/&lt;Name&gt;".</param>
/// <param name="KeyPrefix">"&lt;Plugin-Id&gt;.": Eigenschaften der Ausgabe dürfen den Schlüssel ohne ihn nennen.</param>
public sealed record CustomScriptInfo(string Id, string Name, string Script, int TimeoutSeconds, string KeyPrefix, string Source);

/// <summary>Die eigenen Inventarfelder und Skripte, die Plugins anmelden.</summary>
public interface ICustomFieldCatalog
{
    IReadOnlyList<CustomFieldInfo> Fields { get; }
    IReadOnlyList<CustomScriptInfo> Scripts { get; }

    CustomFieldInfo? Find(string key) => Fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Ohne Plugins: keine Felder, keine Skripte.</summary>
public sealed class EmptyCustomFieldCatalog : ICustomFieldCatalog
{
    public static readonly EmptyCustomFieldCatalog Instance = new();
    public IReadOnlyList<CustomFieldInfo> Fields => [];
    public IReadOnlyList<CustomScriptInfo> Scripts => [];
}
