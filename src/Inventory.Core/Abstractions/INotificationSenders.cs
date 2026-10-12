using System.Net;
using Inventory.Core.Model;

namespace Inventory.Core.Abstractions;

public sealed record MailServer(string Host, int Port, SmtpSecurity Security, NetworkCredential? Credential);

public sealed record MailMessage(string Sender, IReadOnlyList<string> Recipients, string Subject, string Body);

/// <summary>Verschickt eine E-Mail; Zustellfehler kommen als <see cref="RemoteException"/>.</summary>
public interface IMailSender
{
    Task SendAsync(MailServer server, MailMessage message, CancellationToken ct = default);
}

/// <summary>Schickt JSON an einen Webhook; Zustellfehler kommen als <see cref="RemoteException"/>.</summary>
public interface IWebhookSender
{
    Task PostAsync(string url, string json, CancellationToken ct = default);
}
