using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Führt einen gespeicherten Bericht aus und verschickt ihn über die Benachrichtigungskanäle: per E-Mail als Excel-Anhang, an Webhooks als Hinweis mit Link.</summary>
public sealed class ReportMailer(SavedReportService saved, NotificationService notifications, SettingsService settings, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    /// <param name="channelId">null = alle eingeschalteten Kanäle.</param>
    /// <returns>Kurzer Ergebnistext für den Zeitplan.</returns>
    /// <exception cref="InvalidOperationException">Der Bericht fehlt oder kein Kanal hat ihn angenommen.</exception>
    public async Task<string> SendAsync(int reportId, int? channelId, CancellationToken ct = default)
    {
        var report = await saved.GetAsync(reportId, ct) ?? throw new InvalidOperationException(L["Report {0} not found", reportId]);
        var table = await saved.RunAsync(report, ct);
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        var baseUrl = (await settings.GetAsync(SettingKeys.AgentServerUrl, ct))?.TrimEnd('/');
        var text = L["{0} rows, as of {1}.", table.Rows.Count, local.ToString("g")] + (report.Description is null ? "" : "\n" + report.Description);
        var attachment = new MailAttachment($"{SavedReportService.FileName(report)} {local:yyyy-MM-dd}.xlsx", ReportExport.XlsxContentType, ReportExport.Xlsx(table, report.Name));

        var results = await notifications.SendAsync(new Notification(L["Report \"{0}\"", report.Name], text, NotificationSeverity.Info, string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/reports/{report.Id}"),
            channelId is null ? null : [channelId.Value], ct, attachment);
        if (results.Count == 0)
            throw new InvalidOperationException(L["No enabled notification channel to send the report to."]);
        var failed = results.Where(r => r.Error is not null).ToList();
        if (failed.Count == results.Count)
            throw new InvalidOperationException(L["The report could not be sent: {0}", string.Join("; ", failed.Select(f => $"{f.Channel}: {f.Error}"))]);
        return failed.Count == 0
            ? L["Report with {0} rows sent via {1} channels", table.Rows.Count, results.Count]
            : L["Report with {0} rows sent via {1} of {2} channels; failed: {3}", table.Rows.Count, results.Count - failed.Count, results.Count, string.Join(", ", failed.Select(f => f.Channel))];
    }
}
