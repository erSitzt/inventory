using System.Diagnostics;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Microsoft.Extensions.Localization;

namespace Inventory.Remoting;

/// <param name="Installed">Die gefundene Version; null = kein .NET Framework 4.</param>
/// <param name="SelfContainedRuns">Der eigenständige .NET-10-Runner liefe auf dem Rechner (64 Bit, ab Windows 8 / Server 2012).</param>
public sealed record FrameworkState(bool Sufficient, string? Installed, bool SelfContainedRuns);

/// <summary>
/// Der kleine Runner braucht .NET Framework 4.8. Ältere Systeme (Windows 7, Server 2008 R2 bis 2016, frühe Windows-10-Stände)
/// bringen es nicht von Haus aus mit; dort startet er nicht und hinterlässt kein Ergebnis. Ab Windows 8 / Server 2012
/// übernimmt dann der eigenständige .NET-10-Runner, auf älteren Systemen bleibt nur, 4.8 zu installieren.
/// </summary>
public static class NetFramework
{
    /// <summary>Wert „Release“ unter HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full ab .NET Framework 4.8.</summary>
    public const int RequiredRelease = 528040;

    /// <summary>Kennung, mit der das WinRM-Skript ein zu altes Framework meldet: NETFX_MISSING:&lt;Release&gt; (0 = 4.0, -1 = kein 4.x).</summary>
    public const string MissingMarker = "NETFX_MISSING:";

    /// <summary>Der Name der installierten Version zum Release-Wert; null = kein .NET Framework 4.</summary>
    public static string? NameForRelease(int release) => release switch
    {
        < 0 => null,
        >= 533320 => "4.8.1",
        >= 528040 => "4.8",
        >= 461808 => "4.7.2",
        >= 461308 => "4.7.1",
        >= 460798 => "4.7",
        >= 394802 => "4.6.2",
        >= 394254 => "4.6.1",
        >= 393295 => "4.6",
        >= 379893 => "4.5.2",
        >= 378675 => "4.5.1",
        >= 378389 => "4.5",
        _ => "4.0",
    };

    /// <summary>
    /// Die Version aus der Dateiversion von clr.dll: ab 4.6 trägt sie die Framework-Version (4.8.4645.0), davor heißt
    /// sie 4.0.30319.x – unter 17000 ist das 4.0, darüber 4.5 bis 4.5.2.
    /// </summary>
    public static (string Name, bool Sufficient) FromClrVersion(int major, int minor, int build, int revision)
    {
        if (major > 4 || major == 4 && minor >= 8)
            return ($"{major}.{minor}", true);
        if (major == 4 && minor == 0)
            return (build == 30319 && revision >= 17000 ? "4.5" : "4.0", false);
        return ($"{major}.{minor}", false);
    }

    /// <summary>
    /// Liest über ADMIN$ des Zielrechners, welches .NET Framework vorhanden ist und ob der eigenständige .NET-10-Runner
    /// dort liefe. null, wenn sich das nicht feststellen lässt; dann entscheidet der Start des Runners.
    /// </summary>
    public static FrameworkState? Inspect(string adminShare)
    {
        try
        {
            var root = Path.Combine(adminShare, "Microsoft.NET");
            if (!Directory.Exists(root))
                return null;
            var clr = new[] { "Framework64", "Framework" }
                .Select(folder => Path.Combine(root, folder, "v4.0.30319", "clr.dll"))
                .FirstOrDefault(File.Exists);
            string? installed = null;
            if (clr is not null)
            {
                var info = FileVersionInfo.GetVersionInfo(clr);
                var (name, sufficient) = FromClrVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                if (sufficient)
                    return new FrameworkState(true, name, false);
                installed = name;
            }
            return new FrameworkState(false, installed, SelfContainedRuns(adminShare));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// .NET 10 läuft auf 64-Bit-Windows ab Windows 8 / Server 2012 (Kernel 6.2); Windows 7 und Server 2008 R2 bleiben
    /// beim .NET Framework 4.8. 64 Bit erkennt man am Ordner SysWOW64, die Version an kernel32.dll.
    /// </summary>
    private static bool SelfContainedRuns(string adminShare)
    {
        var kernel = Path.Combine(adminShare, "System32", "kernel32.dll");
        if (!Directory.Exists(Path.Combine(adminShare, "SysWOW64")) || !File.Exists(kernel))
            return false;
        var info = FileVersionInfo.GetVersionInfo(kernel);
        return SupportsSelfContained(info.FileMajorPart, info.FileMinorPart);
    }

    public static bool SupportsSelfContained(int major, int minor) => major > 6 || major == 6 && minor >= 2;

    /// <summary>Wählt den Runner für den Rechner: den kleinen, wenn 4.8 da ist oder nichts bekannt ist, sonst den eigenständigen.</summary>
    /// <exception cref="RemoteException">Weder 4.8 noch die eigenständige Variante kommen in Frage.</exception>
    public static RunnerPackage Choose(RunnerPackage runner, FrameworkState? state, IStringLocalizer<SharedResource> L)
    {
        if (state is null or { Sufficient: true })
            return runner;
        if (state.SelfContainedRuns && runner.SelfContained is { } selfContained)
            return selfContained;
        throw new RemoteException(Missing(state.Installed, L), noFallback: true);
    }

    /// <summary>Übersetzt die Meldung des WinRM-Skripts; null, wenn sie nicht von der Framework-Prüfung stammt.</summary>
    public static string? ExplainMarker(string error, IStringLocalizer<SharedResource> L)
    {
        var index = error.IndexOf(MissingMarker, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var digits = new string(error[(index + MissingMarker.Length)..].TakeWhile(c => char.IsDigit(c) || c == '-').ToArray());
        return Missing(int.TryParse(digits, out var release) ? NameForRelease(release) : null, L);
    }

    public static string Missing(string? installed, IStringLocalizer<SharedResource> L) => installed is null
        ? L["The runner needs .NET Framework 4.8, but the computer has no .NET Framework 4 at all. Install .NET Framework 4.8 there (Windows Update, WSUS or the offline installer) and try again."]
        : L["The runner needs .NET Framework 4.8, but the computer only has .NET Framework {0}. Install .NET Framework 4.8 there (Windows Update, WSUS or the offline installer) and try again.", installed];
}
