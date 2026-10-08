using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryAndCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BiosVersion",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CpuCores",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CpuLogicalProcessors",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CpuName",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastBootUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastScanError",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoggedOnUser",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryBytes",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsArchitecture",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OsInstalledUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsName",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsRelease",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsVersion",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsDynamic = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchAll = table.Column<bool>(type: "INTEGER", nullable: false),
                    Rules = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Disks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Drive = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    FileSystem = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FreeBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Disks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Disks_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Hotfixes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    HotfixId = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    InstalledOn = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hotfixes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Hotfixes_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NetworkAdapters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", nullable: true),
                    IpAddresses = table.Column<string>(type: "TEXT", nullable: true),
                    Gateways = table.Column<string>(type: "TEXT", nullable: true),
                    DnsServers = table.Column<string>(type: "TEXT", nullable: true),
                    DhcpEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkAdapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NetworkAdapters_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Services",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    StartMode = table.Column<string>(type: "TEXT", nullable: true),
                    Account = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Services_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Software",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: true),
                    InstallDate = table.Column<string>(type: "TEXT", nullable: true),
                    Is64Bit = table.Column<bool>(type: "INTEGER", nullable: false),
                    PerUser = table.Column<bool>(type: "INTEGER", nullable: false),
                    UninstallString = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Software", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Software_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectionMembers",
                columns: table => new
                {
                    CollectionsId = table.Column<int>(type: "INTEGER", nullable: false),
                    MembersId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionMembers", x => new { x.CollectionsId, x.MembersId });
                    table.ForeignKey(
                        name: "FK_CollectionMembers_Collections_CollectionsId",
                        column: x => x.CollectionsId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectionMembers_Computers_MembersId",
                        column: x => x.MembersId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionMembers_MembersId",
                table: "CollectionMembers",
                column: "MembersId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_Name",
                table: "Collections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Disks_ComputerId",
                table: "Disks",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Hotfixes_ComputerId",
                table: "Hotfixes",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAdapters_ComputerId",
                table: "NetworkAdapters",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Services_ComputerId",
                table: "Services",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Software_ComputerId",
                table: "Software",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Software_Name",
                table: "Software",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionMembers");

            migrationBuilder.DropTable(
                name: "Disks");

            migrationBuilder.DropTable(
                name: "Hotfixes");

            migrationBuilder.DropTable(
                name: "NetworkAdapters");

            migrationBuilder.DropTable(
                name: "Services");

            migrationBuilder.DropTable(
                name: "Software");

            migrationBuilder.DropTable(
                name: "Collections");

            migrationBuilder.DropColumn(
                name: "BiosVersion",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "CpuCores",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "CpuLogicalProcessors",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "CpuName",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LastBootUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LastScanError",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LoggedOnUser",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Manufacturer",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MemoryBytes",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsArchitecture",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsInstalledUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsName",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsRelease",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsVersion",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "Computers");
        }
    }
}
