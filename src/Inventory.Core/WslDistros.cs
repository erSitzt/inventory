using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>
/// WSL-Distributionen stehen in der Registry ihres Benutzers und sind wie die Software pro Benutzer nur lesbar, solange er
/// angemeldet ist. Damit sie nach einer Abmeldung nicht verschwinden, bleiben die Einträge nicht lesbarer Profile aus dem
/// vorigen Scan erhalten (siehe <see cref="PerUserSoftware"/>).
/// </summary>
public static class WslDistros
{
    /// <summary>Ergänzt die Distributionen in <paramref name="current"/> um die zu behaltenden und liefert deren Zahl.</summary>
    public static int CarryOver(IEnumerable<ComputerWslDistro> previous, InventoryData current)
    {
        // Ist WSL selbst nicht mehr da, laufen auch die Distributionen nicht mehr, deren Einträge noch in den Profilen stehen.
        if (current.Wsl is not { Installed: true } wsl || current.UserProfiles is not { } profiles)
            return 0;
        var existing = profiles.Select(p => p.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = profiles.Where(p => p.Loaded).Select(p => p.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = wsl.Distros.Select(d => Key(d.UserSid, d.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = 0;
        foreach (var item in previous)
        {
            // Ein gelesenes Profil ist maßgeblich, ein gelöschtes nimmt seine Distributionen mit.
            if (item.UserSid is null || !existing.Contains(item.UserSid) || loaded.Contains(item.UserSid) || !present.Add(Key(item.UserSid, item.Name)))
                continue;
            wsl.Distros.Add(new WslDistroItem
            {
                Name = item.Name, UserSid = item.UserSid, UserName = item.UserName, WslVersion = item.WslVersion,
                LastUsedUtc = item.LastUsedUtc, SizeBytes = item.SizeBytes, CarriedOver = true,
            });
            kept++;
        }
        return kept;
    }

    /// <summary>Tage seit der jüngsten Nutzung irgendeiner Distribution; null, wenn keine ein Datum hat.</summary>
    public static int? DaysSinceUse(IEnumerable<ComputerWslDistro> distros, DateTime nowUtc) =>
        distros.Max(d => d.LastUsedUtc) is { } last ? Math.Max(0, (int)(nowUtc - last).TotalDays) : null;

    /// <summary>Bezeichnung im Verlauf: Name und Benutzer, denn jeder Benutzer hat eigene Distributionen.</summary>
    public static string Label(string name, string? userName, string? userSid) =>
        (userName ?? userSid) is { } user ? $"{name} ({user})" : name;

    private static string Key(string? sid, string name) => $"{sid}|{name}";
}
