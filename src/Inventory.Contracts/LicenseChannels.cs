using System;

namespace Inventory.Contracts
{
    /// <summary>Lizenzkanäle von Windows in einer einheitlichen Schreibweise.</summary>
    public static class LicenseChannels
    {
        public const string OemDm = "OEM:DM";
        public const string OemSlp = "OEM:SLP";
        public const string OemNonSlp = "OEM:NONSLP";
        public const string Oem = "OEM";
        public const string Retail = "Retail";
        public const string VolumeMak = "Volume:MAK";
        public const string VolumeGvlk = "Volume:GVLK";

        /// <summary>
        /// Aus ProductKeyChannel (ab Windows 8, z. B. "OEM:DM", "Volume:GVLK") oder, wenn es fehlt, aus der Beschreibung des
        /// Lizenzprodukts (z. B. "Windows(R) Operating System, VOLUME_KMSCLIENT channel"). null, wenn nichts davon passt.
        /// </summary>
        public static string? Normalize(string? productKeyChannel, string? description)
        {
            var channel = productKeyChannel?.Trim();
            if (!string.IsNullOrEmpty(channel))
            {
                foreach (var known in new[] { OemDm, OemSlp, OemNonSlp, Retail, VolumeMak, VolumeGvlk })
                    if (string.Equals(channel, known, StringComparison.OrdinalIgnoreCase))
                        return known;
                if (channel!.StartsWith("OEM", StringComparison.OrdinalIgnoreCase))
                    return Oem;
                return channel;
            }
            var text = description?.ToUpperInvariant() ?? "";
            if (text.Contains("OEM_DM"))
                return OemDm;
            if (text.Contains("OEM_SLP") || text.Contains("OEM_COA_SLP"))
                return OemSlp;
            if (text.Contains("OEM_COA_NSLP") || text.Contains("OEM_NONSLP"))
                return OemNonSlp;
            if (text.Contains("OEM"))
                return Oem;
            if (text.Contains("RETAIL"))
                return Retail;
            if (text.Contains("VOLUME_MAK"))
                return VolumeMak;
            if (text.Contains("VOLUME_KMSCLIENT"))
                return VolumeGvlk;
            return null;
        }

        /// <summary>Ein Volumenlizenzschlüssel, der nur mit einem KMS-Server gültig wird.</summary>
        public static bool IsGvlk(string? channel) => string.Equals(channel, VolumeGvlk, StringComparison.Ordinal);

        /// <summary>An die Hardware gebunden (vorinstalliert vom Hersteller).</summary>
        public static bool IsOem(string? channel) => channel != null && channel.StartsWith("OEM", StringComparison.Ordinal);
    }
}
