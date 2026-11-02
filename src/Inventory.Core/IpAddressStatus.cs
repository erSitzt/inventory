namespace Inventory.Core;

/// <summary>Zustand einer Adresse; höhere Werte gewinnen, wenn mehrere Hinweise vorliegen.</summary>
public enum IpAddressStatus
{
    Free,
    /// <summary>Nur alte Spuren: verwaister PTR-Eintrag, lange nicht aufgefrischter DNS-Eintrag, Antwort vor langer Zeit.</summary>
    Stale,
    /// <summary>Ein A-Eintrag (oder bestätigter PTR) zeigt hierher, geantwortet hat niemand – ausgeschaltet oder Ping gesperrt.</summary>
    DnsOnly,
    InUse,
    /// <summary>Liegt in einem ausgenommenen Bereich (z. B. DHCP).</summary>
    Excluded,
    Reserved,
}

public enum IpEvidenceKind
{
    Ping,
    Arp,
    Gateway,
    Computer,
    Device,
    RouterInterface,
    ARecord,
    PtrRecord,
    Excluded,
    Reservation,
}

/// <summary>Ein Hinweis darauf, dass eine Adresse belegt ist oder war.</summary>
/// <param name="Text">Name, MAC-Adresse oder Notiz.</param>
/// <param name="TimeUtc">Wann der Hinweis zuletzt galt (Antwort, Sichtung, DNS-Aktualisierung).</param>
/// <param name="Status">Was dieser Hinweis allein bedeutet.</param>
/// <param name="LinkId">Rechner oder Gerät, zu dem der Hinweis gehört.</param>
public sealed record IpEvidence(IpEvidenceKind Kind, string? Text, DateTime? TimeUtc, IpAddressStatus Status, int? LinkId = null);

/// <summary>Bewertet die Hinweise zu einer Adresse und sucht freie Adressen.</summary>
public static class IpAddressRules
{
    /// <summary>Was in dieser Zeit gesehen wurde, gilt als belegt; ältere Spuren nur als veraltet.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(30);

    public static IpAddressStatus Classify(IEnumerable<IpEvidence> evidence) =>
        evidence.Select(e => e.Status).DefaultIfEmpty(IpAddressStatus.Free).Max();

    /// <summary>Belegt, wenn innerhalb von <see cref="Recent"/> gesehen, sonst veraltet.</summary>
    public static IpAddressStatus BySighting(DateTime? seenUtc, DateTime now) =>
        seenUtc is { } seen && now - seen <= Recent ? IpAddressStatus.InUse : IpAddressStatus.Stale;

    /// <summary>Ein DNS-Eintrag zählt, solange er statisch ist oder kürzlich aufgefrischt wurde.</summary>
    public static IpAddressStatus ByDnsRecord(DateTime? refreshedUtc, DateTime now) =>
        refreshedUtc is { } refreshed && now - refreshed > Recent ? IpAddressStatus.Stale : IpAddressStatus.DnsOnly;

    /// <summary>
    /// Wählt <paramref name="count"/> Adressen aus den Kandidaten (aufsteigend sortierte Adresswerte). Zusammenhängend heißt:
    /// lückenlos aufeinanderfolgende Werte. Leer, wenn nicht genug passen.
    /// </summary>
    public static List<uint> Pick(IReadOnlyList<uint> candidates, int count, bool contiguous, bool fromTop)
    {
        if (count <= 0 || candidates.Count < count)
            return [];
        var ordered = fromTop ? candidates.Reverse().ToList() : [.. candidates];
        if (!contiguous)
            return [.. ordered.Take(count).Order()];
        var run = new List<uint>();
        foreach (var value in ordered)
        {
            // Abstand 1 in Suchrichtung, sonst beginnt eine neue Folge.
            if (run.Count > 0 && (fromTop ? run[^1] - value : value - run[^1]) != 1)
                run.Clear();
            run.Add(value);
            if (run.Count == count)
                return [.. run.Order()];
        }
        return [];
    }
}
