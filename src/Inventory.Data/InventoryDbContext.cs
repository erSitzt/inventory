using System.Text.Json;
using Inventory.Contracts;
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
    public DbSet<ComputerLocalUser> LocalUsers => Set<ComputerLocalUser>();
    public DbSet<ComputerLocalAdmin> LocalAdmins => Set<ComputerLocalAdmin>();
    public DbSet<InventoryChange> InventoryChanges => Set<InventoryChange>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageStep> PackageSteps => Set<PackageStep>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

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
            e.HasMany(c => c.LocalUsers).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.LocalAdmins).WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany<InventoryChange>().WithOne().HasForeignKey(s => s.ComputerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ComputerSoftware>(e =>
        {
            e.Property(s => s.Name).UseCollation("NOCASE");
            e.HasIndex(s => s.Name);
        });

        modelBuilder.Entity<InventoryChange>().HasIndex(c => c.TimeUtc);

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
            e.Property(c => c.MaintenanceWindow).HasConversion(
                window => window == null ? null : JsonSerializer.Serialize(window, JsonSerializerOptions.Default),
                json => json == null ? null : JsonSerializer.Deserialize<MaintenanceWindow>(json, JsonSerializerOptions.Default));
            e.Property(c => c.Rules)
                .HasConversion(
                    rules => JsonSerializer.Serialize(rules, JsonSerializerOptions.Default),
                    json => JsonSerializer.Deserialize<List<CollectionRule>>(json, JsonSerializerOptions.Default) ?? new List<CollectionRule>())
                .Metadata.SetValueComparer(new ValueComparer<List<CollectionRule>>(
                    (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
                    rules => JsonSerializer.Serialize(rules, JsonSerializerOptions.Default).GetHashCode(),
                    rules => JsonSerializer.Deserialize<List<CollectionRule>>(JsonSerializer.Serialize(rules, JsonSerializerOptions.Default), JsonSerializerOptions.Default)!));
        });

        modelBuilder.Entity<Package>(e =>
        {
            e.Property(p => p.Name).UseCollation("NOCASE").HasMaxLength(200);
            e.HasIndex(p => p.Name).IsUnique();
            e.HasMany(p => p.Steps).WithOne().HasForeignKey(s => s.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Collection>().WithMany().HasForeignKey(p => p.CatalogCollectionId).OnDelete(DeleteBehavior.SetNull);
            e.Property(p => p.Detection).HasConversion(
                detection => detection == null ? null : JobJson.Serialize(detection, false),
                json => json == null ? null : JobJson.Deserialize<StepConditions>(json));
        });

        modelBuilder.Entity<PackageStep>(e =>
        {
            // Wird ein eingebettetes Paket gelöscht, verschwindet auch der Schritt, der es aufruft.
            e.HasOne(s => s.NestedPackage).WithMany().HasForeignKey(s => s.NestedPackageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.Definition).HasConversion(
                step => JobJson.Serialize(step, false),
                json => JobJson.Deserialize<JobStep>(json) ?? new JobStep());
        });

        modelBuilder.Entity<Deployment>(e =>
        {
            e.HasMany(d => d.Targets).WithOne().HasForeignKey(t => t.DeploymentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Package>().WithMany().HasForeignKey(d => d.PackageId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DeploymentTarget>(e =>
        {
            e.HasOne<Computer>().WithMany().HasForeignKey(t => t.ComputerId).OnDelete(DeleteBehavior.SetNull);
            e.Property(t => t.Steps)
                .HasConversion(
                    steps => JobJson.Serialize(steps, false),
                    json => JobJson.Deserialize<List<StepResult>>(json) ?? new List<StepResult>())
                .Metadata.SetValueComparer(new ValueComparer<List<StepResult>>(
                    (a, b) => ReferenceEquals(a, b),
                    steps => steps.Count,
                    steps => steps.ToList()));
        });

        modelBuilder.Entity<Schedule>(e =>
        {
            e.HasOne(s => s.Package).WithMany().HasForeignKey(s => s.PackageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Collection).WithMany().HasForeignKey(s => s.CollectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiToken>().HasIndex(t => t.TokenHash).IsUnique();

        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
    }
}
