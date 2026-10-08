using System.Text.Json;
using Inventory.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Inventory.Data;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<Computer> Computers => Set<Computer>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<ComputerSoftware> Software => Set<ComputerSoftware>();
    public DbSet<ComputerHotfix> Hotfixes => Set<ComputerHotfix>();
    public DbSet<ComputerDisk> Disks => Set<ComputerDisk>();
    public DbSet<ComputerNetworkAdapter> NetworkAdapters => Set<ComputerNetworkAdapter>();
    public DbSet<ComputerWindowsService> Services => Set<ComputerWindowsService>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Computer>(e =>
        {
            e.Property(c => c.Name).UseCollation("NOCASE").HasMaxLength(253);
            e.HasIndex(c => c.Name).IsUnique();
            e.HasOne(c => c.Credential).WithMany().HasForeignKey(c => c.CredentialId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.Software).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Hotfixes).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Disks).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.NetworkAdapters).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Services).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ComputerSoftware>(e =>
        {
            e.Property(s => s.Name).UseCollation("NOCASE");
            e.HasIndex(s => s.Name);
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.Property(c => c.Name).UseCollation("NOCASE").HasMaxLength(200);
            e.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<Collection>(e =>
        {
            e.Property(c => c.Name).UseCollation("NOCASE").HasMaxLength(200);
            e.HasIndex(c => c.Name).IsUnique();
            e.HasMany(c => c.Members).WithMany(c => c.Collections).UsingEntity("CollectionMembers");
            e.Property(c => c.Rules)
                .HasConversion(
                    rules => JsonSerializer.Serialize(rules, JsonSerializerOptions.Default),
                    json => JsonSerializer.Deserialize<List<CollectionRule>>(json, JsonSerializerOptions.Default) ?? new List<CollectionRule>())
                .Metadata.SetValueComparer(new ValueComparer<List<CollectionRule>>(
                    (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
                    rules => JsonSerializer.Serialize(rules, JsonSerializerOptions.Default).GetHashCode(),
                    rules => JsonSerializer.Deserialize<List<CollectionRule>>(JsonSerializer.Serialize(rules, JsonSerializerOptions.Default), JsonSerializerOptions.Default)!));
        });

        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
    }
}
