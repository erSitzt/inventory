using Inventory.Core;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Inventory.Server.Services;

/// <summary>Ein Halt der Tour: das hervorgehobene Element (CSS-Selektor; null = mittig ohne Element) und seine Erklärung.</summary>
public sealed record TourStep(string? Selector, string Title, string Text);

/// <summary>
/// Geführte Tour durch Menü und Leiste (wwwroot/js/tour.js). Die Elemente tragen ein Attribut data-tour; Schritte,
/// deren Element die eigene Rolle nicht sieht, überspringt die Tour.
/// </summary>
public sealed class GuidedTour(IJSRuntime js, IStringLocalizer<SharedResource> L)
{
    public IReadOnlyList<TourStep> Steps() =>
    [
        new(null, L["Welcome to Inventory"], L["This short tour shows where things are. Arrow keys move on and back, Esc ends it; the question mark at the top right starts it again."]),
        new(Target("attention"), L["Needs attention"], L["What deserves a look right now – firewall off, antivirus problems, pending updates and reboots. Each line leads to the computers behind it."]),
        new(Target("computers"), L["Computers"], L["Every computer with its status and inventory. Start here: add computers or import them from Active Directory, then scan them."]),
        new(Target("run"), L["Remote command"], L["Runs a command or PowerShell on several computers at once, with live output."]),
        new(Target("devices"), L["Devices"], L["Printers, switches, routers and other network devices, found by scanning address ranges – with type, model and toner levels."]),
        new(Target("collections"), L["Collections"], L["Groups of computers – picked by hand or kept up to date by rules such as \"Chrome older than 141\". Deployments, schedules and reports work on them."]),
        new(Target("packages"), L["Packages"], L["What to install or change, as a list of steps: setups, scripts, registry, files, reboots."]),
        new(Target("library"), L["Package library"], L["Ready-made packages of common programs, imported with one click and kept up to date."]),
        new(Target("deployments"), L["Deployments"], L["Every run of a package on computers, live per computer and step."]),
        new(Target("schedules"), L["Schedules"], L["Scans, deployments and report mails that run on their own – for example a package for every new laptop."]),
        new(Target("software"), L["Software, licences, updates, changes"], L["What is installed, which licences are used up, which updates are missing and what changed since the last scan."]),
        new(Target("reports"), L["Reports"], L["Saved lists as CSV, Excel or PDF, or by mail on a schedule."]),
        new(Target("settings"), L["Settings"], L["Active Directory, agents, transport, maintenance window, server updates and backup."]),
        new(Target("help"), L["Help for this page"], L["Explains the page you are on, with a link to the documentation. The tour starts from here, too."]),
    ];

    /// <summary>Das Layout öffnet daraufhin das Menü und startet die Tour; so funktioniert der Start von jeder Seite aus gleich.</summary>
    public event Func<Task>? StartRequested;

    /// <summary>Tour aus einer Seite heraus starten (über das Layout, damit das Menü sichtbar ist).</summary>
    public Task RequestAsync() => StartRequested?.Invoke() ?? StartAsync();

    /// <summary>Startet die Tour sofort.</summary>
    public async Task StartAsync()
    {
        try
        {
            await js.InvokeVoidAsync("inventoryTour.start", Steps(), new { next = L["Next"].Value, back = L["Back"].Value, done = L["Done"].Value, close = L["End tour"].Value });
        }
        catch (JSException)
        {
            // Ohne Skript (z. B. blockiert) gibt es keine Tour; die Seite selbst bleibt benutzbar.
        }
    }

    /// <summary>Startet die Tour, wenn sie in diesem Browser noch nie gezeigt wurde.</summary>
    public async Task StartOnceAsync()
    {
        try
        {
            if (!await js.InvokeAsync<bool>("inventoryTour.seen"))
                await StartAsync();
        }
        catch (JSException)
        {
        }
    }

    private static string Target(string name) => $"[data-tour=\"{name}\"]";
}
