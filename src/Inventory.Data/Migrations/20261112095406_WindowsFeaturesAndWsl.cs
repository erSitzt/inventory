using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class WindowsFeaturesAndWsl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FeaturesScanUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WslInstalled",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WslScanUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WslVersion",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OptionalFeatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionalFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptionalFeatures_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WslDistros",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UserSid = table.Column<string>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", nullable: true),
                    WslVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CarriedOver = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WslDistros", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WslDistros_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionalFeatures_ComputerId",
                table: "OptionalFeatures",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_WslDistros_ComputerId",
                table: "WslDistros",
                column: "ComputerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionalFeatures");

            migrationBuilder.DropTable(
                name: "WslDistros");

            migrationBuilder.DropColumn(
                name: "FeaturesScanUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "WslInstalled",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "WslScanUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "WslVersion",
                table: "Computers");
        }
    }
}
