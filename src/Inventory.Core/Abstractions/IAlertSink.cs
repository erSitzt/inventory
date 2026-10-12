using System.Collections.Concurrent;
using Inventory.Core.Model;

namespace Inventory.Core.Abstractions;

/// <param name="Path">Seite der Oberfläche zum Ereignis, z. B. "schedules"; daraus wird mit der Server-Adresse ein Link.</param>
public sealed record AlertEvent(AlertKind Kind, string Title, string Text, string? Path = null)
{
    /// <summary>So oft wurde schon vergeblich versucht, das Ereignis zuzustellen.</summary>
    public int Attempts { get; init; }
}

/// <summary>Hier melden Dienste Ereignisse, für die es Alarmregeln geben kann. Melden blockiert nie und schlägt nie fehl.</summary>
public interface IAlertSink
{
    void Raise(AlertEvent alert);
}

/// <summary>
/// Sammelt gemeldete Ereignisse, bis der Takt sie auswertet. So hängt kein Dienst von den Benachrichtigungen ab,
/// und ein langsamer Mailserver hält weder Zeitplan noch Abgleich auf.
/// </summary>
public sealed class AlertQueue : IAlertSink
{
    private const int Limit = 500;
    private readonly ConcurrentQueue<AlertEvent> _events = new();

    public void Raise(AlertEvent alert)
    {
        // Ohne Abnehmer (Takt steht) soll die Warteschlange nicht unbegrenzt wachsen; die ältesten Meldungen fallen weg.
        while (_events.Count >= Limit && _events.TryDequeue(out _))
        {
        }
        _events.Enqueue(alert);
    }

    public List<AlertEvent> Drain()
    {
        var result = new List<AlertEvent>();
        while (_events.TryDequeue(out var alert))
            result.Add(alert);
        return result;
    }
}
