using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Inventory.Data;

/// <summary>Nur für <c>dotnet ef migrations</c>; zur Laufzeit konfiguriert der Server den Kontext.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
