using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AntivirusDefinitionsOutdated",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AntivirusProductsInactive",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DefenderLastQuickScanUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefenderMode",
                table: "Computers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AntivirusDefinitionsOutdated",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "AntivirusProductsInactive",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderLastQuickScanUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderMode",
                table: "Computers");
        }
    }
}
