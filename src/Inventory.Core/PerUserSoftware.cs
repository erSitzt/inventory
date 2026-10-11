using Inventory.Contracts;
using Inventory.Core.Model;

namespace Inventory.Core;

/// <summary>
/// Pro Benutzer installierte Programme stehen im Profil des Benutzers und sind nur lesbar, solange er angemeldet ist.
/// Damit sie nach einer Abmeldung oder einem Neustart nicht als "entfernt" erscheinen, bleiben die Einträge nicht lesbarer
/// Profile aus dem vorigen Scan erhalten, bis das Profil wieder gelesen werden kann oder gelöscht ist.
/// </summary>
public static class PerUserSoftware
{
    /// <summary>Ergänzt <paramref name="current"/> um die zu behaltenden Einträge und liefert deren Zahl.</summary>
    public static int CarryOver(IEnumerable<ComputerSoftware> previous, InventoryData current)
    {
        // Ältere Runner melden die Profile nicht: dann bleibt es beim gelesenen Stand.
        if (current.UserProfiles is not { } profiles)
            return 0;
        var existing = profiles.Select(p => p.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = profiles.Where(p => p.Loaded).Select(p => p.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = current.Software.Select(Key).ToHashSet();

        var kept = 0;
        foreach (var item in previous.Where(s => s.PerUser))
        {
            var keep = item.UserSid is null
                // Einträge aus der Zeit vor dieser Zuordnung: nur behalten, solange gar kein Profil lesbar ist.
                ? loaded.Count == 0
                // Ein gelesenes Profil ist maßgeblich, ein gelöschtes Profil nimmt seine Programme mit.
                : existing.Contains(item.UserSid) && !loaded.Contains(item.UserSid);
            if (!keep || !present.Add((item.Name, item.Version, item.Is64Bit, true)))
                continue;
            current.Software.Add(new SoftwareItem
            {
                Name = item.Name, Version = item.Version, Publisher = item.Publisher, InstallDate = item.InstallDate, Is64Bit = item.Is64Bit,
                PerUser = true, UninstallString = item.UninstallString, UserSid = item.UserSid, CarriedOver = true,
            });
            kept++;
        }
        return kept;
    }

    private static (string, string?, bool, bool) Key(SoftwareItem s) => (s.Name, s.Version, s.Is64Bit, s.PerUser);
}
