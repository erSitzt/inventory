using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailySnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Computers = table.Column<int>(type: "INTEGER", nullable: false),
                    Online = table.Column<int>(type: "INTEGER", nullable: false),
                    Scanned = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanErrors = table.Column<int>(type: "INTEGER", nullable: false),
                    StaleScans = table.Column<int>(type: "INTEGER", nullable: false),
                    FirewallOff = table.Column<int>(type: "INTEGER", nullable: false),
                    AntivirusProblems = table.Column<int>(type: "INTEGER", nullable: false),
                    RebootRequired = table.Column<int>(type: "INTEGER", nullable: false),
                    WithPendingUpdates = table.Column<int>(type: "INTEGER", nullable: false),
                    DriveUnprotected = table.Column<int>(type: "INTEGER", nullable: false),
                    WarrantyEnding = table.Column<int>(type: "INTEGER", nullable: false),
                    Agents = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailySnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailySnapshots_Date",
                table: "DailySnapshots",
                column: "Date",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailySnapshots");
        }
    }
}
