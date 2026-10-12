using System.Net.Sockets;
using System.Text;
using Inventory.Core.Abstractions;
using Inventory.Core.Model;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Inventory.Remoting;

public sealed class MailKitSender : IMailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(MailServer server, MailMessage message, CancellationToken ct = default)
    {
        try
        {
            var mail = new MimeMessage { Subject = message.Subject, Body = new TextPart("plain") { Text = message.Body } };
            mail.From.Add(MailboxAddress.Parse(message.Sender));
            foreach (var recipient in message.Recipients)
                mail.To.Add(MailboxAddress.Parse(recipient));

            using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
            await client.ConnectAsync(server.Host, server.Port, server.Security switch
            {
                SmtpSecurity.Ssl => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            }, ct);
            if (server.Credential is not null)
                await client.AuthenticateAsync(server.Credential, ct);
            await client.SendAsync(mail, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is MailKit.ProtocolException or MailKit.CommandException or AuthenticationException or SslHandshakeException
                                       or SocketException or IOException or ParseException or TimeoutException && !ct.IsCancellationRequested)
        {
            throw new RemoteException(ex.Message, ex);
        }
    }
}

public sealed class HttpWebhookSender(HttpClient http) : IWebhookSender
{
    /// <summary>
    /// Entfernt die Adresse und ihre Bestandteile aus einem Text. Die Antwort des Dienstes (oder eines Proxys davor) kann
    /// die aufgerufene Adresse wiederholen.
    /// </summary>
    internal static string Redact(string text, string url)
    {
        var parts = new List<string> { url };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            parts.Add(uri.AbsoluteUri);
            parts.Add(uri.PathAndQuery);
            parts.AddRange(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
            parts.AddRange(uri.Query.TrimStart('?').Split(['&', '='], StringSplitOptions.RemoveEmptyEntries));
            parts.AddRange(parts.ToList().Select(Uri.UnescapeDataString));
        }
        // Kurze Stücke ("api", "v1") verraten nichts und kämen in jedem Text vor.
        foreach (var part in parts.Where(p => p.Length >= 6).Distinct().OrderByDescending(p => p.Length))
            text = text.Replace(part, "…", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    public async Task PostAsync(string url, string json, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode)
                return;
            var body = await response.Content.ReadAsStringAsync(ct);
            // Die Adresse selbst nie in die Meldung: sie enthält das Geheimnis des Webhooks, und die Meldung wird am Kanal gespeichert.
            var detail = Redact(body.Length <= 200 ? body : body[..200] + "…", url);
            throw new RemoteException($"HTTP {(int)response.StatusCode}" + (detail.Length == 0 ? "" : ": " + detail));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RemoteException(ex is TaskCanceledException ? "Timeout" : (ex.InnerException?.Message ?? ex.Message), ex);
        }
    }
}
