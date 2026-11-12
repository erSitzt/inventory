using System;

namespace Inventory.Contracts
{
    /// <summary>Welche SIDs zu Benutzern gehören, deren Profil und Registry der Runner liest.</summary>
    public static class UserSids
    {
        /// <summary>Lokale und Domänenkonten (S-1-5-21-…) und Konten aus Entra ID (S-1-12-1-…); keine Dienst- und Systemkonten.</summary>
        public static bool IsUser(string? sid) =>
            sid != null && (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal));

        /// <summary>Geladene Registry eines Benutzers unter HKEY_USERS, ohne den Zweig &lt;SID&gt;_Classes.</summary>
        public static bool IsUserHive(string name) => IsUser(name) && !name.EndsWith("_Classes", StringComparison.Ordinal);
    }
}
