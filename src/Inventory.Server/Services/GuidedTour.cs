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
        new(Target("network"), L["Network map"], L["Subnets, routers, devices and computers as an interactive diagram."]),
        new(Target("ip-addresses"), L["IP addresses"], L["Used and free addresses per subnet, from ping, ARP, DNS and the inventory. Finds free IPs – also as a contiguous block – and reserves them."]),
        new(Target("virtualization"), L["Virtualization"], L["Hyper-V, vCenter and XCP-ng hosts with their virtual machines, matched to the inventoried computers."]),
        new(Target("collections"), L["Collections"], L["Groups of computers – picked by hand or kept up to date by rules such as \"Chrome older than 141\". Deployments, schedules and reports work on them."]),
        new(Target("packages"), L["Packages"], L["What to install or change, as a list of steps: setups, scripts, registry, files, reboots."]),
        new(Target("library"), L["Package library"], L["Ready-made packages of common programs, imported with one click and kept up to date."]),
        new(Target("deployments"), L["Deployments"], L["Every run of a package on computers, live per computer and step."]),
        new(Target("schedules"), L["Schedules"], L["Scans, deployments and report mails that run on their own – for example a package for every new laptop."]),
        new(Target("software"), L["Software"], L["Which programs and versions are installed where, including Store apps."]),
        new(Target("licenses"), L["Licences"], L["The licences you own against the installations found – which are used up or exceeded."]),
        new(Target("licensing"), L["Microsoft licensing"], L["An estimate of the Windows Server and SQL Server licences needed per host and cluster, against the licences you own."]),
        new(Target("updates"), L["Updates"], L["Missing Windows updates across all computers, security updates first – installed with one click or on a schedule."]),
        new(Target("changes"), L["Changes"], L["What changed between scans: software, hardware, services, local administrators and more."]),
        new(Target("reports"), L["Reports"], L["Saved lists as CSV, Excel or PDF, or by mail on a schedule."]),
        new(Target("credentials"), L["Credentials"], L["Accounts for agentless access to the computers, stored encrypted."]),
        new(Target("notifications"), L["Notifications"], L["Messages by mail or webhook (Teams, Slack …) when something happens – a failed deployment, a new device, a full licence."]),
        new(Target("tokens"), L["API tokens"], L["Tokens for scripts, the REST API and AI assistants (MCP), each with its own role."]),
        new(Target("audit"), L["Audit log"], L["Who did what and when – in the web interface, through the API or an AI assistant."]),
        new(Target("settings"), L["Settings"], L["Active Directory, agents, transport, maintenance window, AI assistants (MCP), export and import, server updates and backup."]),
        new(Target("plugins"), L["Plugins"], L["Extensions with their own pages, actions, jobs and inventory data, switched on here by an administrator."]),
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
