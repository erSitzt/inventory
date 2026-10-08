using Inventory.Core.Abstractions;
using Inventory.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Tests;

/// <summary>SQLite im Arbeitsspeicher; die offene Verbindung hält die Datenbank für die Dauer des Tests am Leben.</summary>
internal sealed class TestDb : IDbContextFactory<InventoryDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<InventoryDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public InventoryDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
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
