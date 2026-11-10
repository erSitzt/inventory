using System.Diagnostics;
using System.Text;

namespace Inventory.Agent.MacOS;

/// <summary>Startet Programme des Systems und liest ihre Ausgabe; bricht nach einer Frist ab.</summary>
internal static class Shell
{
    private static readonly object Sync = new();
    private static Process? _current;

    /// <summary>Ausgabe des Programms; null, wenn es nicht startet, scheitert oder die Frist überschreitet.</summary>
    /// <param name="acceptExitCodes">Exitcodes, bei denen die Ausgabe trotzdem gilt (z. B. 1 bei defaults ohne Eintrag).</param>
    public static string? Run(string file, IEnumerable<string> arguments, TimeSpan? timeout = null, params int[] acceptExitCodes)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            lock (Sync)
                _current = process;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(60)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            process.WaitForExit();
            _ = error.Result;
            return process.ExitCode == 0 || acceptExitCodes.Contains(process.ExitCode) ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            lock (Sync)
                _current = null;
        }
    }

    public static string? Run(string file, params string[] arguments) => Run(file, arguments, null);

    /// <summary>Beendet das gerade laufende Programm (Abbruch des Jobs).</summary>
    public static void KillCurrent()
    {
        Process? process;
        lock (Sync)
            process = _current;
        try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
