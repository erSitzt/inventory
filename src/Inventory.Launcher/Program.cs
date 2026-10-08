using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Inventory.Launcher
{
    /// <summary>
    /// Starter für den Admin-PC: nimmt Links der Form inventory://aktion/rechner entgegen, die die Weboberfläche
    /// von Inventory erzeugt, und öffnet die passende Windows-Fernverbindung.
    ///
    /// Solche Links kann jede Webseite auslösen. Deshalb gibt es nur eine feste Liste von Aktionen, Rechnername und
    /// Sitzungsnummer werden streng geprüft, und gestartet werden ausschließlich msra.exe und mstsc.exe aus System32.
    /// </summary>
    internal static class Program
    {
        private const string Title = "Inventory-Starter";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Register(Registry.CurrentUser);
                    MessageBox.Show("Der Starter ist für dieses Benutzerkonto eingerichtet.\n\nFernzugriffe aus Inventory öffnen sich jetzt direkt. " +
                                    "Diese Datei bitte nicht verschieben oder löschen – sonst einfach erneut starten.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                switch (args[0])
                {
                    case "--register":
                        Register(Registry.CurrentUser);
                        return 0;
                    case "--register-machine":
                        Register(Registry.LocalMachine);
                        return 0;
                    case "--unregister":
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + LinkParser.Scheme, false);
                        return 0;
                }

                if (!LinkParser.TryParse(args[0], out var fileName, out var arguments, out var error))
                {
                    MessageBox.Show("Dieser Link wird nicht unterstützt:\n" + error, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 2;
                }
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, fileName), arguments) { UseShellExecute = false });
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>Trägt dieses Programm als Ziel für inventory://-Links ein (für den Benutzer oder, mit Adminrechten, für den ganzen PC).</summary>
        private static void Register(RegistryKey root)
        {
            var path = Assembly.GetExecutingAssembly().Location;
            using (var key = root.CreateSubKey(@"Software\Classes\" + LinkParser.Scheme))
            {
                key.SetValue("", "URL:Inventory-Fernzugriff");
                key.SetValue("URL Protocol", "");
                using (var command = key.CreateSubKey(@"shell\open\command"))
                    command.SetValue("", "\"" + path + "\" \"%1\"");
            }
        }
    }
}
