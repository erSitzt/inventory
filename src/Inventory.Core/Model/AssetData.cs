namespace Inventory.Core.Model;

/// <summary>Von Hand gepflegte Angaben zu einem Gerät, wie sie sonst in einer Bestandsliste stehen.</summary>
public sealed record AssetData(string? Owner, string? Location, string? CostCenter, string? InventoryNumber, DateOnly? PurchaseDate, DateOnly? WarrantyUntil, string? Tags, string? Notes);

public static class AssetTags
{
    /// <summary>Tags aus freier Eingabe: durch Komma oder Semikolon getrennt, ohne Doppelte, in der eingegebenen Schreibweise.</summary>
    public static List<string> Parse(string? text) =>
        (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Gespeicherte Form: mit ", " verbunden; null, wenn es keine gibt.</summary>
    public static string? Normalize(string? text) => Parse(text) is { Count: > 0 } tags ? string.Join(", ", tags) : null;
}

/// <summary>Benutzerprofil auf einem Rechner.</summary>
public class ComputerUserProfile
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    /// <summary>DOMÄNE\\name; lässt sich die SID nicht auflösen, der Name des Profilordners.</summary>
    public required string UserName { get; set; }
    public string? Sid { get; set; }
    /// <summary>Letzte Nutzung laut Windows; der Wert ändert sich auch durch Wartungsvorgänge und ist nur ein Anhaltspunkt.</summary>
    public DateTime? LastUseUtc { get; set; }
    /// <summary>Der Benutzer war beim Scan angemeldet.</summary>
    public bool Loaded { get; set; }
}

/// <summary>Ein Tag, an dem ein Benutzer bei einem Scan an der Konsole angemeldet war; daraus ergibt sich der Hauptbenutzer.</summary>
public class ComputerUserSighting
{
    public long Id { get; set; }
    public int ComputerId { get; set; }
    public required string UserName { get; set; }
    public DateOnly Day { get; set; }
}
