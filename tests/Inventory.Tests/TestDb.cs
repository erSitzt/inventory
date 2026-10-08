using Inventory.Core.Abstractions;
using Inventory.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>
/// SQLite-Datei im Temp-Verzeichnis. Wie im Betrieb bekommt jeder Kontext eine eigene Verbindung,
/// sodass parallele Zugriffe (Scans, Deployments) realistisch getestet werden.
/// </summary>
internal sealed class TestDb : IDbContextFactory<InventoryDbContext>, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"inventory-test-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<InventoryDbContext> _options;

    public TestDb()
    {
        _options = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public InventoryDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
    }
}

internal sealed class ReversingProtector : ISecretProtector
{
    public string Protect(string plaintext) => new(plaintext.Reverse().ToArray());
    public string Unprotect(string protectedValue) => new(protectedValue.Reverse().ToArray());
}

internal sealed class FixedProbe(Func<string, ProbeResult> result) : IHostProbe
{
    public Task<ProbeResult> ProbeAsync(string host, CancellationToken ct = default) => Task.FromResult(result(host));
}
