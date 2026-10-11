using System.Runtime.InteropServices;
using System.Text;

namespace Inventory.Data;

/// <summary>Liest Angaben aus der Property-Tabelle einer MSI-Datei (nur unter Windows).</summary>
public static class MsiProperties
{
    public sealed record Product(string? Name, string? Version, string? ProductCode);

    /// <summary>null, wenn die Datei kein lesbares MSI ist oder der Server nicht unter Windows läuft.</summary>
    public static Product? Read(string path)
    {
        if (!OperatingSystem.IsWindows() || MsiOpenDatabaseW(path, IntPtr.Zero, out var database) != 0)
            return null;
        try
        {
            if (MsiDatabaseOpenViewW(database, "SELECT `Property`, `Value` FROM `Property`", out var view) != 0)
                return null;
            try
            {
                if (MsiViewExecute(view, 0) != 0)
                    return null;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (MsiViewFetch(view, out var record) == 0)
                {
                    values[Text(record, 1)] = Text(record, 2);
                    MsiCloseHandle(record);
                }
                return new Product(values.GetValueOrDefault("ProductName"), values.GetValueOrDefault("ProductVersion"), values.GetValueOrDefault("ProductCode"));
            }
            finally
            {
                MsiCloseHandle(view);
            }
        }
        finally
        {
            MsiCloseHandle(database);
        }
    }

    private static string Text(uint record, uint field)
    {
        var size = 0u;
        MsiRecordGetStringW(record, field, new StringBuilder(1), ref size);
        size++;
        var buffer = new StringBuilder((int)size);
        return MsiRecordGetStringW(record, field, buffer, ref size) == 0 ? buffer.ToString() : "";
    }

    // MSIHANDLE ist auch unter 64 Bit ein 32-Bit-Wert, kein Zeiger.
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out uint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(uint view, uint record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(uint view, out uint record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder buffer, ref uint size);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(uint handle);
}
