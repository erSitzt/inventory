using System.Globalization;
using Inventory.Core;

namespace Inventory.Server.Services;

/// <summary>Sprachen, für die Übersetzungen vorliegen: Englisch (Quelltext) plus jede Satellitenassembly von Inventory.Core.</summary>
public static class Languages
{
    public const string Source = "en";

    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var assembly = Path.GetFileNameWithoutExtension(typeof(SharedResource).Assembly.Location);
        var codes = Directory.GetDirectories(AppContext.BaseDirectory)
            .Where(dir => File.Exists(Path.Combine(dir, assembly + ".resources.dll")))
            .Select(dir => Path.GetFileName(dir).ToLowerInvariant())
            .Where(IsCulture)
            .Append(Source)
            .Distinct()
            .Order();
        return codes.Select(code => (code, NativeName(code))).ToList();
    }

    private static bool IsCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).Name.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static string NativeName(string code)
    {
        if (code == Source)
            return "English";
        var culture = CultureInfo.GetCultureInfo(code);
        return char.ToUpper(culture.NativeName[0], culture) + culture.NativeName[1..];
    }
}
