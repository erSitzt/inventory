using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssetFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CostCenter",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InventoryNumber",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryUser",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PurchaseDate",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WarrantyUntil",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    Sid = table.Column<string>(type: "TEXT", nullable: true),
                    LastUseUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Loaded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserProfiles_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSightings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSightings_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_ComputerId",
                table: "UserProfiles",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSightings_ComputerId_UserName_Day",
                table: "UserSightings",
                columns: new[] { "ComputerId", "UserName", "Day" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "UserSightings");

            migrationBuilder.DropColumn(
                name: "CostCenter",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "InventoryNumber",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "PrimaryUser",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "PurchaseDate",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "WarrantyUntil",
                table: "Computers");
        }
    }
}
