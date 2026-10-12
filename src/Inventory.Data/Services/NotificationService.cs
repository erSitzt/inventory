using System.Text.Json;
using Inventory.Core;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Inventory.Data.Services;

/// <summary>Ergebnis eines Versands an einen Kanal; <see cref="Error"/> ist null, wenn die Nachricht angenommen wurde.</summary>
public sealed record NotificationResult(string Channel, string? Error);

/// <summary>Verwaltet die Benachrichtigungskanäle und verschickt Nachrichten darüber.</summary>
public sealed class NotificationService(IDbContextFactory<InventoryDbContext> dbFactory, ISecretProtector protector, CredentialService credentials,
    IMailSender mail, IWebhookSender webhooks, TimeProvider time, IStringLocalizer<SharedResource>? localizer = null)
{
    private readonly IStringLocalizer<SharedResource> L = localizer ?? PassthroughLocalizer.Instance;

    public async Task<List<NotificationChannel>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.NotificationChannels.AsNoTracking().Include(c => c.Credential).OrderBy(c => c.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Legt einen Kanal an (Id 0) oder ändert ihn. Die Adresse eines Webhooks wird verschlüsselt abgelegt und nie wieder
    /// angezeigt; eine leere Adresse lässt die gespeicherte unverändert.
    /// </summary>
    /// <exception cref="ArgumentException">Pflichtangaben fehlen oder sind ungültig.</exception>
    public async Task<NotificationChannel> SaveAsync(NotificationChannel input, string? webhookUrl, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = input.Id == 0 ? null : await db.NotificationChannels.FindAsync([input.Id], ct) ?? throw new KeyNotFoundException(L["Channel {0} not found", input.Id]);
        if (channel is null)
        {
            channel = new NotificationChannel { Name = input.Name.Trim(), Kind = input.Kind, CreatedUtc = time.GetUtcNow().UtcDateTime };
            db.NotificationChannels.Add(channel);
        }
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException(L["Name is missing"]);
        channel.Name = input.Name.Trim();
        channel.Enabled = input.Enabled;

        if (channel.Kind == NotificationKind.Email)
        {
            var recipients = NotificationChannel.ParseRecipients(input.Recipients);
            if (string.IsNullOrWhiteSpace(input.SmtpHost))
                throw new ArgumentException(L["The mail server is missing."]);
            if (!IsAddress(input.Sender))
                throw new ArgumentException(L["The sender is not a valid e-mail address."]);
            if (recipients.Count == 0)
                throw new ArgumentException(L["At least one recipient is required."]);
            if (recipients.FirstOrDefault(r => !IsAddress(r)) is { } invalid)
                throw new ArgumentException(L["\"{0}\" is not a valid e-mail address.", invalid]);
            channel.SmtpHost = input.SmtpHost.Trim();
            channel.SmtpPort = Math.Clamp(input.SmtpPort, 1, 65535);
            channel.SmtpSecurity = input.SmtpSecurity;
            channel.CredentialId = input.CredentialId;
            channel.Sender = input.Sender!.Trim();
            channel.Recipients = string.Join(", ", recipients);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new ArgumentException(L["The webhook address must start with https:// or http://."]);
                channel.ProtectedUrl = protector.Protect(uri.AbsoluteUri);
            }
            else if (channel.ProtectedUrl is null)
                throw new ArgumentException(L["The webhook address is missing."]);
            channel.WebhookFormat = input.WebhookFormat;
        }
        await db.SaveChangesAsync(ct);
        return channel;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Schickt eine Probenachricht an genau diesen Kanal, auch wenn er abgeschaltet ist.</summary>
    public async Task<NotificationResult> TestAsync(int id, CancellationToken ct = default) =>
        (await SendCoreAsync(new Notification(L["Test message from Inventory"],
            L["If you can read this, the channel works. Inventory uses it to report events you choose."]), [id], includeDisabled: true, ct)).Single();

    /// <summary>
    /// Verschickt an die genannten Kanäle (null = alle eingeschalteten). Wirft nie wegen eines Zustellfehlers: das Ergebnis
    /// je Kanal kommt zurück und steht am Kanal.
    /// </summary>
    public Task<List<NotificationResult>> SendAsync(Notification notification, IReadOnlyCollection<int>? channelIds = null, CancellationToken ct = default) =>
        SendCoreAsync(notification, channelIds, includeDisabled: false, ct);

    private async Task<List<NotificationResult>> SendCoreAsync(Notification notification, IReadOnlyCollection<int>? channelIds, bool includeDisabled, CancellationToken ct)
    {
        List<NotificationChannel> channels;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            channels = await db.NotificationChannels.AsNoTracking()
                .Where(c => (includeDisabled || c.Enabled) && (channelIds == null || channelIds.Contains(c.Id))).OrderBy(c => c.Name).ToListAsync(ct);
        }

        var results = new List<NotificationResult>();
        foreach (var channel in channels)
        {
            string? error = null;
            try
            {
                if (channel.Kind == NotificationKind.Email)
                {
                    var login = channel.CredentialId is null ? null : await credentials.GetAsync(channel.CredentialId, ct);
                    await mail.SendAsync(new MailServer(channel.SmtpHost!, channel.SmtpPort, channel.SmtpSecurity, login),
                        new MailMessage(channel.Sender!, NotificationChannel.ParseRecipients(channel.Recipients), Subject(notification), Body(notification)), ct);
                }
                else
                {
                    await webhooks.PostAsync(protector.Unprotect(channel.ProtectedUrl!), Payload(channel.WebhookFormat, notification, time.GetUtcNow().UtcDateTime), ct);
                }
            }
            catch (RemoteException ex)
            {
                error = ex.Message;
            }
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var now = time.GetUtcNow().UtcDateTime;
                await db.NotificationChannels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.LastError, error)
                    .SetProperty(c => c.LastSentUtc, c => error == null ? now : c.LastSentUtc), ct);
            }
            results.Add(new NotificationResult(channel.Name, error));
        }
        return results;
    }

    /// <summary>Großzügige Prüfung: genau ein @ mit etwas davor und einem Punkt-Namen danach. Den Rest entscheidet der Mailserver.</summary>
    internal static bool IsAddress(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) || value.Any(c => c is '<' or '>' or ',' or ';'))
            return false;
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1;
    }

    internal static string Subject(Notification notification) => notification.Severity switch
    {
        NotificationSeverity.Error => "[Inventory] ✖ ",
        NotificationSeverity.Warning => "[Inventory] ⚠ ",
        _ => "[Inventory] ",
    } + OneLine(notification.Title);

    internal static string Body(Notification notification) =>
        notification.Text.ReplaceLineEndings("\r\n") + (notification.Link is null ? "" : "\r\n\r\n" + notification.Link);

    /// <summary>Betreffzeilen und Überschriften dürfen keinen Zeilenumbruch tragen (sonst ließen sich Kopfzeilen einschleusen).</summary>
    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Die Nachricht in der Form, die der Dienst hinter dem Webhook erwartet.</summary>
    internal static string Payload(WebhookFormat format, Notification notification, DateTime timeUtc)
    {
        var title = OneLine(notification.Title);
        object payload = format switch
        {
            WebhookFormat.Teams => new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = new Dictionary<string, object>
                        {
                            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                            ["type"] = "AdaptiveCard",
                            ["version"] = "1.4",
                            ["body"] = new object[]
                            {
                                new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true, color = notification.Severity switch
                                {
                                    NotificationSeverity.Error => "Attention",
                                    NotificationSeverity.Warning => "Warning",
                                    _ => "Default",
                                } },
                                new { type = "TextBlock", text = notification.Text, wrap = true },
                            },
                            ["actions"] = notification.Link is null ? [] : new object[] { new Dictionary<string, object> { ["type"] = "Action.OpenUrl", ["title"] = "Inventory", ["url"] = notification.Link } },
                        },
                    },
                },
            },
            WebhookFormat.Slack => new
            {
                text = title,
                blocks = new object[]
                {
                    new { type = "header", text = new { type = "plain_text", text = title.Length <= 150 ? title : title[..150] } },
                    new { type = "section", text = new { type = "mrkdwn", text = SlackEscape(notification.Text) + (notification.Link is null ? "" : $"\n<{notification.Link}|Inventory>") } },
                },
            },
            _ => new
            {
                source = "Inventory",
                title,
                text = notification.Text,
                severity = notification.Severity.ToString().ToLowerInvariant(),
                link = notification.Link,
                timeUtc = timeUtc.ToString("O"),
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string SlackEscape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
