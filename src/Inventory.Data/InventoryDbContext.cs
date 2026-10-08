using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Data;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<Computer> Computers => Set<Computer>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Computer>(e =>
        {
            e.Property(c => c.Name).UseCollation("NOCASE").HasMaxLength(253);
            e.HasIndex(c => c.Name).IsUnique();
            e.HasOne(c => c.Credential).WithMany().HasForeignKey(c => c.CredentialId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.Property(c => c.Name).UseCollation("NOCASE").HasMaxLength(200);
            e.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
    }
}
