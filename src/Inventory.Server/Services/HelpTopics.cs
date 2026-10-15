using Inventory.Core;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Services;

/// <summary>Hilfe zu einer Seite: kurzer Zweck und die Handgriffe, die neue Benutzer zuerst brauchen.</summary>
public sealed record HelpTopic(string Title, string Intro, IReadOnlyList<string> Tips);

/// <summary>Hilfetexte je Seite, ausgewählt über den Pfad der aktuellen Adresse.</summary>
public static class HelpTopics
{
    public static HelpTopic For(string relativePath, IStringLocalizer<SharedResource> L)
    {
        var parts = relativePath.Split('?', '#')[0].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var page = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var detail = parts.Length > 1;
        return (page, detail) switch
        {
            ("computers", true) => new(L["Computer details"], L["Everything the last scan found about one computer, plus its asset data and history."],
            [
                L["The tabs show hardware, software, apps, hotfixes, disks, network, services, security, local users, profiles and the change history."],
                L["\"Scan now\" refreshes the inventory; \"Run command\" and \"Remote access\" act on this computer only."],
                L["\"Edit\" holds the asset data (owner, location, warranty …), the credential and the transport for this computer."],
            ]),
            ("computers", false) => new(L["Computers"], L["All computers Inventory knows, with reachability, last scan and security indicators."],
            [
                L["Add computers by name, import them from Active Directory (Settings) or let the agent enrol them."],
                L["Select computers with the checkboxes to scan, deploy, run a command, wake them or install the agent."],
                L["The filters and the search narrow the list; the CSV export can be edited and imported again for asset data."],
                L["Scans need a stored credential with local administrator rights on the computers, or the agent."],
            ]),
            ("run", _) => new(L["Remote command"], L["Runs a command or a short PowerShell script on several computers at once and shows the output live."],
            [
                L["\"Local System\" has full rights on the computer; \"Logged-on user\" runs in the session of whoever is signed in."],
                L["Nothing runs on computers that are offline; they show an error instead."],
                L["For anything you need more than once, build a package instead – it can be scheduled and repeated."],
            ]),
            ("collections", _) => new(L["Collections"], L["Groups of computers that deployments, schedules, reports and alerts work on."],
            [
                L["Static collections hold the computers you pick; dynamic ones follow rules and update with every scan."],
                L["Rules combine software, hardware, security, updates, Active Directory and asset data; \"member of collection\" nests them."],
                L["A collection can carry a maintenance window; deployments with \"only within the maintenance window\" respect it."],
            ]),
            ("packages", true) => new(L["Package"], L["The steps a deployment runs on each computer, in order."],
            [
                L["\"Add step\" offers installation, command, PowerShell, files, registry, Windows feature, service, reboot, scan and more."],
                L["Upload the setup files under \"Files\"; installation steps pick them from there."],
                L["Detection skips computers that already have the software; prerequisites run other packages first."],
                L["An administrator approves the package before operators and schedules may deploy it; changes withdraw the approval."],
            ]),
            ("packages", false) => new(L["Packages"], L["Software and configuration you distribute, each as a list of steps."],
            [
                L["Create a package yourself, or import a ready-made one from the package library."],
                L["Open a package to edit its steps, files and detection, and to deploy it."],
                L["Only approved packages can be deployed by operators and schedules."],
            ]),
            ("library", _) => new(L["Package library"], L["Ready-made packages of common programs, generated daily from the winget manifests."],
            [
                L["\"Import\" downloads the setup and creates a package with silent switches and detection."],
                L["Imported packages can follow new versions automatically, together with a dynamic \"… outdated\" collection."],
                L["A schedule \"package to its outdated collection\" keeps every computer current."],
            ]),
            ("deployments", true) => new(L["Deployment"], L["The progress of one deployment, per computer and per step."],
            [
                L["Expand a computer to see the output of every step."],
                L["Waiting computers say why: offline, outside their maintenance window, rebooting, postponed by the user or waiting for the pilot group."],
                L["\"Cancel\" stops computers that have not started; failed ones can be retried."],
            ]),
            ("deployments", false) => new(L["Deployments"], L["Every run of a package on a set of computers, newest first."],
            [
                L["\"Deploy package\" starts a new one; packages and the computer list offer the same."],
                L["Limits, a pilot group, Wake-on-LAN and maintenance windows are options of each deployment."],
            ]),
            ("schedules", _) => new(L["Schedules"], L["Scans, deployments and report mails that run on their own."],
            [
                L["Required deployments give each member of a collection the package once, including computers that join later."],
                L["Monthly schedules can follow the patch day, e.g. three days after the second Tuesday."],
                L["The play button runs a schedule once right away."],
            ]),
            ("software", _) => new(L["Software"], L["Every program found by the scans, counted across all computers."],
            [
                L["Select a program to see the computers and versions that have it."],
                L["\"Uninstall …\" removes it from those computers through a deployment."],
            ]),
            ("licenses", _) => new(L["Licences"], L["Owned licences compared with the installations the inventory counts."],
            [
                L["The name pattern decides which programs count, e.g. \"Microsoft Office*\"."],
                L["An alert rule \"A licence is exceeded\" reports when installations outgrow the owned licences."],
            ]),
            ("updates", _) => new(L["Updates"], L["Updates that Windows reports as pending, per update and per computer."],
            [
                L["Select an update to see which computers miss it; \"Install …\" deploys it to them."],
                L["The list comes from the scans; computers that were not scanned recently may be out of date."],
            ]),
            ("changes", _) => new(L["Changes"], L["What changed between two scans: software, hotfixes, services, local administrators, hardware and configuration."],
            [
                L["Search by computer, program or user; the same history is on each computer's \"History\" tab."],
            ]),
            ("reports", _) => new(L["Reports"], L["Saved lists of computers, software, updates, deployments or changes."],
            [
                L["Open a report to view it, or export it as CSV, Excel or PDF."],
                L["The examples can be used as they are or as templates; a schedule can mail a report regularly."],
            ]),
            ("credentials", _) => new(L["Credentials"], L["Accounts the server uses for agentless access to the computers."],
            [
                L["The account needs local administrator rights on the computers – usually a domain account added by group policy."],
                L["The default credential is used unless a computer names its own."],
                L["Passwords are stored encrypted and never shown again."],
            ]),
            ("notifications", _) => new(L["Notifications"], L["Channels for e-mail and webhooks, and the rules that send messages through them."],
            [
                L["Add a channel first and send a test; then add alert rules that use it."],
                L["Alert rules report changes only – what already applies when a rule is created is the starting point."],
            ]),
            ("tokens", _) => new(L["API tokens"], L["Keys for scripts and other systems that use the REST API."],
            [
                L["A token has a role and acts with its rights; the secret is shown only once."],
                L["The interactive API documentation is at /api/docs."],
            ]),
            ("audit", _) => new(L["Audit log"], L["Who signed in, ran something or changed something – with role and source."],
            [
                L["Filter by user, action, period and text; the CSV export takes the filter along."],
            ]),
            ("settings", _) => new(L["Settings"], L["Server-wide options: transport, Active Directory, agents, maintenance window, updates and backup."],
            [
                L["Active Directory imports computers regularly; agents connect computers the server cannot reach."],
                L["Agentless access can be restricted to installing the agent, or switched off."],
            ]),
            _ => new(L["Overview"], L["The state of all computers at a glance and what needs attention."],
            [
                L["\"Getting started\" lists the first steps until they are done."],
                L["\"Needs attention\" leads to the computers behind each number."],
                L["The trends show one value per day for the last 30 days."],
            ]),
        };
    }
}
