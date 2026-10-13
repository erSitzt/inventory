using System;
using System.Text;

namespace Inventory.Contracts
{
    /// <summary>Regeln rund um App-Pakete (MSIX/AppX); von Server und Runner gemeinsam genutzt.</summary>
    public static class AppNames
    {
        /// <summary>Trifft ein Muster mehr verschiedene Apps, wird nichts entfernt: dann war es zu weit gefasst.</summary>
        public const int MaxMatches = 5;

        /// <summary>Laufzeitbibliotheken, von denen andere Apps abhängen; sie werden nie entfernt und nicht als App gezeigt.</summary>
        public static readonly string[] LibraryPrefixes =
        {
            "Microsoft.VCLibs.", "Microsoft.NET.", "Microsoft.UI.Xaml.", "Microsoft.WindowsAppRuntime.", "Microsoft.DirectXRuntime", "Microsoft.Services.Store.Engagement",
        };

        /// <summary>
        /// true, wenn das Muster an den Entfernen-Schritt gehen darf: nur Zeichen eines Paketnamens und *, und genug festen Text,
        /// dass es nicht "alles" oder "alles von Microsoft" bedeutet.
        /// </summary>
        public static bool IsRemovalPattern(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern!.Length > 128)
                return false;
            var literal = 0;
            foreach (var c in pattern)
            {
                if (c == '*')
                    continue;
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '.' && c != '-')
                    return false;
                literal++;
            }
            if (literal < 4)
                return false;
            if (pattern.IndexOf('*') < 0)
                return true;
            if (literal < 6)
                return false;
            // "Hersteller*" oder "Hersteller.*" träfe alle Apps dieses Herstellers: der Platzhalter braucht mehr als das erste Namensglied.
            var fixedText = pattern.Trim('*');
            var vendorOnly = pattern[0] != '*' && fixedText.IndexOf('*') < 0 && fixedText.TrimEnd('.').IndexOf('.') < 0;
            return !vendorOnly;
        }

        /// <summary>Zerlegt einen vollständigen Paketnamen (Name_Version_Architektur_Ressource_Herausgeberkennung).</summary>
        public static bool TryParseFullName(string fullName, out string name, out string version, out string architecture, out string resourceId, out string publisherId)
        {
            name = version = architecture = resourceId = publisherId = "";
            var parts = (fullName ?? "").Split('_');
            if (parts.Length != 5 || parts[0].Length == 0 || parts[4].Length == 0)
                return false;
            name = parts[0];
            version = parts[1];
            architecture = parts[2];
            resourceId = parts[3];
            publisherId = parts[4];
            return true;
        }

        /// <summary>
        /// PowerShell-Skript des Entfernen-Schritts. Entfernt wird erst, wenn feststeht, was das Muster trifft: Framework- und
        /// Systempakete bleiben außen vor, und bei mehr als <see cref="MaxMatches"/> verschiedenen Apps geschieht nichts.
        /// </summary>
        public static string RemovalScript(string pattern, bool keepProvisioned)
        {
            if (!IsRemovalPattern(pattern))
                throw new System.IO.InvalidDataException("\"" + pattern + "\" ist kein gültiger App-Name");
            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            script.AppendLine("$pattern = '" + pattern.Trim() + "'");
            script.AppendLine("$matching = @(Get-AppxPackage -AllUsers -Name $pattern)");
            script.AppendLine("$installed = @($matching | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and $_.SignatureKind -ne 'System' -and -not $_.NonRemovable })");
            // Bereitgestellte Pakete tragen diese Merkmale nicht: geschützt ist, was installiert als geschützt erkannt wurde,
            // und was dem Namen nach eine Laufzeitbibliothek ist.
            script.AppendLine("$protected = @($matching | Where-Object { $_.IsFramework -or $_.IsResourcePackage -or $_.SignatureKind -eq 'System' -or $_.NonRemovable } | ForEach-Object { $_.Name })");
            script.AppendLine("$libraries = @(" + string.Join(", ", System.Linq.Enumerable.Select(LibraryPrefixes, prefix => "'" + prefix + "*'")) + ")");
            script.AppendLine(keepProvisioned
                ? "$provisioned = @()"
                : "$provisioned = @(Get-AppxProvisionedPackage -Online | Where-Object { $name = $_.DisplayName; $name -like $pattern -and $protected -notcontains $name -and @($libraries | Where-Object { $name -like $_ }).Count -eq 0 })");
            script.AppendLine("$names = @(@($installed | ForEach-Object { $_.Name }) + @($provisioned | ForEach-Object { $_.DisplayName }) | Sort-Object -Unique)");
            script.AppendLine("if ($names.Count -eq 0) { Write-Output \"No app matches $pattern; nothing to remove.\"; exit 0 }");
            script.AppendLine("if ($names.Count -gt " + MaxMatches + ") { Write-Output (\"The pattern $pattern matches \" + $names.Count + \" apps: \" + ($names -join ', ') + \". Nothing was removed; narrow the pattern down.\"); exit 2 }");
            script.AppendLine("foreach ($package in $provisioned) { Remove-AppxProvisionedPackage -Online -PackageName $package.PackageName | Out-Null; Write-Output (\"No longer provisioned for new users: \" + $package.DisplayName) }");
            script.AppendLine("foreach ($package in $installed) { Remove-AppxPackage -Package $package.PackageFullName -AllUsers; Write-Output (\"Removed: \" + $package.PackageFullName) }");
            script.AppendLine("exit 0");
            return script.ToString();
        }
    }
}
