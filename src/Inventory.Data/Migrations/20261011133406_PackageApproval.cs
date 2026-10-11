using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Packages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedUtc",
                table: "Packages",
                type: "TEXT",
                nullable: true);

            // Vorhandene Pakete wurden bisher ohne Freigabe verteilt und bleiben verteilbar.
            migrationBuilder.Sql("UPDATE Packages SET ApprovedBy = '(before approvals)', ApprovedUtc = strftime('%Y-%m-%d %H:%M:%f', 'now')");
            // Wer Bibliothekspakete schon automatisch aktualisiert, behält das Verhalten: sonst blieben Zeitpläne nach dem nächsten Update stehen.
            migrationBuilder.Sql("INSERT INTO Settings (Key, Value) SELECT 'Packages.AutoApproveLibraryUpdates', 'True' WHERE EXISTS (SELECT 1 FROM Packages WHERE CatalogAutoUpdate = 1) AND NOT EXISTS (SELECT 1 FROM Settings WHERE Key = 'Packages.AutoApproveLibraryUpdates')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "ApprovedUtc",
                table: "Packages");
        }
    }
}
