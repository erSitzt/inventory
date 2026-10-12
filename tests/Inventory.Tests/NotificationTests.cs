using System.Net;
using System.Text.Json;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using Inventory.Data.Services;

namespace Inventory.Tests;

public class NotificationTests
{
    private sealed class Rig : IDisposable
    {
        public TestDb Db { get; } = new();
        public RecordingMail Mail { get; } = new();
        public RecordingWebhook Webhook { get; } = new();
        public CredentialService Credentials { get; }
        public NotificationService Service { get; }

        public Rig()
        {
            Credentials = new CredentialService(Db, new ReversingProtector());
            Service = new NotificationService(Db, new ReversingProtector(), Credentials, Mail, Webhook, TimeProvider.System);
        }

        public Task<NotificationChannel> MailChannelAsync(string name = "IT", int? credentialId = null, bool enabled = true) =>
            Service.SaveAsync(new NotificationChannel
            {
                Name = name, Kind = NotificationKind.Email, Enabled = enabled, SmtpHost = "mail.example.local", SmtpPort = 587,
                CredentialId = credentialId, Sender = "inventory@example.local", Recipients = "anna@example.local; bernd@example.local\nanna@example.local",
            }, null);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Mail_goes_to_all_recipients_with_the_stored_sign_in()
    {
        using var rig = new Rig();
        var login = await rig.Credentials.SaveAsync(null, "Mail", "mailer", "geheim", null);
        var channel = await rig.MailChannelAsync(credentialId: login.Id);
        Assert.Equal("anna@example.local, bernd@example.local", channel.Recipients);

        var results = await rig.Service.SendAsync(new Notification("Deployment failed\r\nBcc: x@example.org", "3 of 10 computers failed.", NotificationSeverity.Error, "https://inventory.example.local/deployments/7"));

        Assert.Equal([new NotificationResult("IT", null)], results);
        var (server, message) = rig.Mail.Sent.Single();
        Assert.Equal(("mail.example.local", 587, SmtpSecurity.StartTls, "mailer", "geheim"), (server.Host, server.Port, server.Security, server.Credential!.UserName, server.Credential.Password));
        Assert.Equal(["anna@example.local", "bernd@example.local"], message.Recipients);
        // Ein Zeilenumbruch im Titel darf keine weitere Kopfzeile ergeben.
        Assert.Equal("[Inventory] ✖ Deployment failed Bcc: x@example.org", message.Subject);
        Assert.EndsWith("https://inventory.example.local/deployments/7", message.Body);
        Assert.NotNull((await rig.Service.ListAsync()).Single().LastSentUtc);
    }

    [Fact]
    public async Task Without_sign_in_no_credentials_are_used_even_if_a_default_exists()
    {
        using var rig = new Rig();
        await rig.Credentials.SaveAsync(null, "Standard", @"CORP\deploy", "pw", null);
        await rig.MailChannelAsync();

        await rig.Service.SendAsync(new Notification("Titel", "Text"));

        Assert.Null(rig.Mail.Sent.Single().Server.Credential);
    }

    [Fact]
    public async Task Failures_are_recorded_per_channel_and_do_not_stop_the_others()
    {
        using var rig = new Rig();
        await rig.MailChannelAsync("A Mail");
        await rig.Service.SaveAsync(new NotificationChannel { Name = "B Chat", Kind = NotificationKind.Webhook, WebhookFormat = WebhookFormat.Slack }, "https://hooks.example.test/T000/secret");
        await rig.MailChannelAsync("C Aus", enabled: false);
        rig.Mail.Fail = "Relay access denied";

        var results = await rig.Service.SendAsync(new Notification("Titel", "Text"));

        Assert.Equal([new NotificationResult("A Mail", "Relay access denied"), new NotificationResult("B Chat", null)], results);
        var stored = await rig.Service.ListAsync();
        Assert.Equal(("Relay access denied", (DateTime?)null), (stored[0].LastError, stored[0].LastSentUtc));
        Assert.Null(stored[1].LastError);
        Assert.Equal("https://hooks.example.test/T000/secret", rig.Webhook.Sent.Single().Url);
        // In der Datenbank liegt die Adresse nicht im Klartext.
        Assert.DoesNotContain("hooks.example.test", stored[1].ProtectedUrl);

        // Der Test eines Kanals erreicht auch einen abgeschalteten, ein erneuter Erfolg löscht den Fehler.
        rig.Mail.Fail = null;
        Assert.Null((await rig.Service.TestAsync(stored[2].Id)).Error);
        Assert.Null((await rig.Service.TestAsync(stored[0].Id)).Error);
        Assert.Null((await rig.Service.ListAsync())[0].LastError);
    }

    [Fact]
    public async Task Invalid_channels_are_rejected_and_an_empty_address_keeps_the_stored_one()
    {
        using var rig = new Rig();
        NotificationChannel Mail(string? host = "mail", string? sender = "a@b.example", string? recipients = "c@d.example") =>
            new() { Name = "X", Kind = NotificationKind.Email, SmtpHost = host, Sender = sender, Recipients = recipients };

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SaveAsync(Mail(host: " "), null));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SaveAsync(Mail(sender: "kein-absender"), null));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SaveAsync(Mail(recipients: ""), null));
        Assert.Contains("x@", (await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SaveAsync(Mail(recipients: "c@d.example, x@"), null))).Message);
        var hook = new NotificationChannel { Name = "Hook", Kind = NotificationKind.Webhook };
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SaveAsync(hook, null));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SaveAsync(hook, "file:///etc/passwd"));
        Assert.Empty(await rig.Service.ListAsync());

        var saved = await rig.Service.SaveAsync(hook, "https://hooks.example.test/a");
        await rig.Service.SaveAsync(new NotificationChannel { Id = saved.Id, Name = "Hook neu", Kind = NotificationKind.Webhook, WebhookFormat = WebhookFormat.Teams }, "");
        await rig.Service.SendAsync(new Notification("Titel", "Text"));

        Assert.Equal("https://hooks.example.test/a", rig.Webhook.Sent.Single().Url);
        Assert.Equal(("Hook neu", WebhookFormat.Teams), ((await rig.Service.ListAsync()).Single().Name, (await rig.Service.ListAsync()).Single().WebhookFormat));
    }

    [Fact]
    public void Webhook_payloads_match_the_service()
    {
        var notification = new Notification("Scan <failed>", "5 & more\nsecond line", NotificationSeverity.Warning, "https://inventory.example.local/x");
        var time = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

        var generic = JsonDocument.Parse(NotificationService.Payload(WebhookFormat.Generic, notification, time)).RootElement;
        Assert.Equal(("Inventory", "Scan <failed>", "warning", "https://inventory.example.local/x", "2026-10-12T08:00:00.0000000Z"),
            (generic.GetProperty("source").GetString(), generic.GetProperty("title").GetString(), generic.GetProperty("severity").GetString(),
                generic.GetProperty("link").GetString(), generic.GetProperty("timeUtc").GetString()));

        var card = JsonDocument.Parse(NotificationService.Payload(WebhookFormat.Teams, notification, time)).RootElement.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("contentType").GetString());
        var content = card.GetProperty("content");
        Assert.Equal(("AdaptiveCard", "Scan <failed>", "Warning", "5 & more\nsecond line"), (content.GetProperty("type").GetString(),
            content.GetProperty("body")[0].GetProperty("text").GetString(), content.GetProperty("body")[0].GetProperty("color").GetString(), content.GetProperty("body")[1].GetProperty("text").GetString()));
        Assert.Equal("https://inventory.example.local/x", content.GetProperty("actions")[0].GetProperty("url").GetString());
        Assert.Equal(0, JsonDocument.Parse(NotificationService.Payload(WebhookFormat.Teams, notification with { Link = null }, time)).RootElement
            .GetProperty("attachments")[0].GetProperty("content").GetProperty("actions").GetArrayLength());

        var slack = JsonDocument.Parse(NotificationService.Payload(WebhookFormat.Slack, notification, time)).RootElement;
        Assert.Equal("Scan <failed>", slack.GetProperty("text").GetString());
        Assert.Equal("5 &amp; more\nsecond line\n<https://inventory.example.local/x|Inventory>", slack.GetProperty("blocks")[1].GetProperty("text").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Mail_sender_speaks_smtp_and_reports_refusals()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var transcript = new List<string>();
        async Task ServeAsync(bool acceptRecipient)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 test ESMTP");
            var data = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                transcript.Add(line);
                if (data)
                {
                    if (line != ".")
                        continue;
                    data = false;
                    await writer.WriteLineAsync("250 queued");
                }
                else if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250 test");
                else if (line.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase) && !acceptRecipient)
                    await writer.WriteLineAsync("550 Relay access denied");
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    data = true;
                    await writer.WriteLineAsync("354 go ahead");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else
                    await writer.WriteLineAsync("250 ok");
            }
        }
        var sender = new Inventory.Remoting.MailKitSender();
        var server = new MailServer("127.0.0.1", port, SmtpSecurity.None, null);
        var message = new MailMessage("inventory@example.local", ["anna@example.local"], "[Inventory] Test", "Zeile 1\r\nZeile 2");

        var serving = ServeAsync(acceptRecipient: true);
        await sender.SendAsync(server, message);
        await serving;
        Assert.Contains(transcript, l => l.StartsWith("MAIL FROM:<inventory@example.local>", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transcript, l => l.StartsWith("RCPT TO:<anna@example.local>", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Subject: [Inventory] Test", transcript);

        serving = ServeAsync(acceptRecipient: false);
        var refused = await Assert.ThrowsAsync<RemoteException>(() => sender.SendAsync(server, message));
        Assert.Contains("Relay access denied", refused.Message);

        listener.Stop();
        await Assert.ThrowsAsync<RemoteException>(() => sender.SendAsync(server, message));
    }

    [Fact]
    public async Task Webhook_errors_never_contain_the_address()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "invalid token");
        var sender = new Inventory.Remoting.HttpWebhookSender(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<RemoteException>(() => sender.PostAsync("https://hooks.example.test/T000/secret", "{}"));

        Assert.Equal("HTTP 403: invalid token", error.Message);
        Assert.Equal(("https://hooks.example.test/T000/secret", "{}", "application/json"), (handler.Url, handler.Body, handler.ContentType));
        var unreachable = new Inventory.Remoting.HttpWebhookSender(new HttpClient(new StubHandler(null, "")));
        Assert.DoesNotContain("secret", (await Assert.ThrowsAsync<RemoteException>(() => unreachable.PostAsync("https://hooks.example.test/T000/secret", "{}"))).Message);
    }

    private sealed class StubHandler(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public string? Url, Body, ContentType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            (Url, Body, ContentType) = (request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct), request.Content.Headers.ContentType?.MediaType);
            if (status is null)
                throw new HttpRequestException("Name or service not known");
            return new HttpResponseMessage(status.Value) { Content = new StringContent(body) };
        }
    }

    private sealed class RecordingMail : IMailSender
    {
        public List<(MailServer Server, MailMessage Message)> Sent { get; } = [];
        public string? Fail { get; set; }

        public Task SendAsync(MailServer server, MailMessage message, CancellationToken ct = default)
        {
            if (Fail is not null)
                throw new RemoteException(Fail);
            Sent.Add((server, message));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWebhook : IWebhookSender
    {
        public List<(string Url, string Json)> Sent { get; } = [];

        public Task PostAsync(string url, string json, CancellationToken ct = default)
        {
            Sent.Add((url, json));
            return Task.CompletedTask;
        }
    }
}
