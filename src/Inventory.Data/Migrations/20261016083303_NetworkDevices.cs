using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetworkDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkRanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Addresses = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectedCommunities = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    LastScanUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkRanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetworkDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", nullable: true),
                    MacVendor = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    DiscoveredName = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedKind = table.Column<int>(type: "INTEGER", nullable: false),
                    KindOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    KindReason = table.Column<string>(type: "TEXT", nullable: true),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Firmware = table.Column<string>(type: "TEXT", nullable: true),
                    OsGuess = table.Column<string>(type: "TEXT", nullable: true),
                    OpenPorts = table.Column<string>(type: "TEXT", nullable: true),
                    PingReply = table.Column<bool>(type: "INTEGER", nullable: false),
                    SnmpResponded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SysDescr = table.Column<string>(type: "TEXT", nullable: true),
                    SysObjectId = table.Column<string>(type: "TEXT", nullable: true),
                    SysLocation = table.Column<string>(type: "TEXT", nullable: true),
                    SysContact = table.Column<string>(type: "TEXT", nullable: true),
                    UptimeSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    PageCount = table.Column<long>(type: "INTEGER", nullable: true),
                    SshBanner = table.Column<string>(type: "TEXT", nullable: true),
                    Banner = table.Column<string>(type: "TEXT", nullable: true),
                    HttpServer = table.Column<string>(type: "TEXT", nullable: true),
                    HttpTitle = table.Column<string>(type: "TEXT", nullable: true),
                    TlsSubject = table.Column<string>(type: "TEXT", nullable: true),
                    ReverseName = table.Column<string>(type: "TEXT", nullable: true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: true),
                    NetworkRangeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NetworkDevices_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NetworkDevices_NetworkRanges_NetworkRangeId",
                        column: x => x.NetworkRangeId,
                        principalTable: "NetworkRanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DeviceSupplies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NetworkDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSupplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceSupplies_NetworkDevices_NetworkDeviceId",
                        column: x => x.NetworkDeviceId,
                        principalTable: "NetworkDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSupplies_NetworkDeviceId",
                table: "DeviceSupplies",
                column: "NetworkDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkDevices_ComputerId",
                table: "NetworkDevices",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkDevices_IpAddress",
                table: "NetworkDevices",
                column: "IpAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetworkDevices_MacAddress",
                table: "NetworkDevices",
                column: "MacAddress");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkDevices_NetworkRangeId",
                table: "NetworkDevices",
                column: "NetworkRangeId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkRanges_Name",
                table: "NetworkRanges",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceSupplies");

            migrationBuilder.DropTable(
                name: "NetworkDevices");

            migrationBuilder.DropTable(
                name: "NetworkRanges");
        }
    }
}
