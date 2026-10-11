using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardwareFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BatteryCycleCount",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BatteryHealthPercent",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BatteryPresent",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BiosDateUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChassisType",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirmwareType",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gpu",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemorySlots",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SecureBoot",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SystemDriveProtected",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TpmPresent",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TpmReady",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TpmVersion",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryModules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<string>(type: "TEXT", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    PartNumber = table.Column<string>(type: "TEXT", nullable: true),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: true),
                    CapacityBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    SpeedMhz = table.Column<int>(type: "INTEGER", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryModules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryModules_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Monitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Monitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Monitors_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhysicalDisks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", nullable: true),
                    BusType = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Health = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalDisks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhysicalDisks_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Printers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Driver = table.Column<string>(type: "TEXT", nullable: true),
                    Port = table.Column<string>(type: "TEXT", nullable: true),
                    Network = table.Column<bool>(type: "INTEGER", nullable: false),
                    Shared = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Printers_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Volumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Drive = table.Column<string>(type: "TEXT", nullable: false),
                    Protected = table.Column<bool>(type: "INTEGER", nullable: true),
                    EncryptedPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    Method = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Volumes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Volumes_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryModules_ComputerId",
                table: "MemoryModules",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Monitors_ComputerId",
                table: "Monitors",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalDisks_ComputerId",
                table: "PhysicalDisks",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Printers_ComputerId",
                table: "Printers",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Volumes_ComputerId",
                table: "Volumes",
                column: "ComputerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryModules");

            migrationBuilder.DropTable(
                name: "Monitors");

            migrationBuilder.DropTable(
                name: "PhysicalDisks");

            migrationBuilder.DropTable(
                name: "Printers");

            migrationBuilder.DropTable(
                name: "Volumes");

            migrationBuilder.DropColumn(
                name: "BatteryCycleCount",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "BatteryHealthPercent",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "BatteryPresent",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "BiosDateUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "ChassisType",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "FirmwareType",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Gpu",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MemorySlots",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "SecureBoot",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "SystemDriveProtected",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "TpmPresent",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "TpmReady",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "TpmVersion",
                table: "Computers");
        }
    }
}
