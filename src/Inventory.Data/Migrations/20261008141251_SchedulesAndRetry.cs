using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchedulesAndRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RetryUntilUtc",
                table: "Deployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: true),
                    ScanAfter = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryOffline = table.Column<bool>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    StartLocal = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeOfDay = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    DaysOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Schedules_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Schedules_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_CollectionId",
                table: "Schedules",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_PackageId",
                table: "Schedules",
                column: "PackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Schedules");

            migrationBuilder.DropColumn(
                name: "RetryUntilUtc",
                table: "Deployments");
        }
    }
}
