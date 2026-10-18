namespace Inventory.Server.Services;

/// <summary>
/// Die Zeitzone des Browsers für die laufende Sitzung. Der Browser legt sie als Cookie ab (<see cref="Cookie"/>, IANA-Name wie
/// "Europe/Berlin"); ohne Cookie oder bei unbekanntem Namen gilt die Zeitzone des Servers.
/// </summary>
public sealed class BrowserTime
{
    public const string Cookie = "inventory.tz";

    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Local;

    /// <summary>Übernimmt die Zeitzone aus dem Cookie; unbekannte Namen werden ignoriert.</summary>
    public void Set(string? id)
    {
        if (Resolve(id) is { } zone)
            Zone = zone;
    }

    /// <summary>Die Zeitzone zu einem IANA-Namen aus dem Browser; null, wenn sie fehlt oder unbekannt ist.</summary>
    public static TimeZoneInfo? Resolve(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    /// <summary>UTC-Zeitpunkt (Werte aus der Datenbank tragen keine Kennzeichnung) in der Ortszeit des Browsers.</summary>
    public DateTime ToLocal(DateTime utc) => utc.Kind switch
    {
        DateTimeKind.Local => TimeZoneInfo.ConvertTime(utc, Zone),
        _ => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone),
    };

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);

    /// <summary>Ein Kalendertag des Browsers (z. B. aus einem Datumsfilter) als UTC-Beginn dieses Tages.</summary>
    public DateTime StartOfDayUtc(DateTime day) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), Zone);
}
