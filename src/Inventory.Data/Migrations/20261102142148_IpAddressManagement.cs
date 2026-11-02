using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class IpAddressManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpSubnets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Cidr = table.Column<string>(type: "TEXT", nullable: false),
                    Vlan = table.Column<int>(type: "INTEGER", nullable: true),
                    Gateway = table.Column<string>(type: "TEXT", nullable: true),
                    Excluded = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    ScanEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScanIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    LastScanUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpSubnets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReplyUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MacAddress = table.Column<string>(type: "TEXT", nullable: true),
                    MacSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PtrName = table.Column<string>(type: "TEXT", nullable: true),
                    PtrConfirmed = table.Column<bool>(type: "INTEGER", nullable: true),
                    PtrRefreshedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ARecords = table.Column<string>(type: "TEXT", nullable: true),
                    ARefreshedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpObservations_IpSubnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "IpSubnets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IpReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ReservedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpReservations_IpSubnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "IpSubnets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IpObservations_SubnetId_Address",
                table: "IpObservations",
                columns: new[] { "SubnetId", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpReservations_Address",
                table: "IpReservations",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpReservations_SubnetId",
                table: "IpReservations",
                column: "SubnetId");

            migrationBuilder.CreateIndex(
                name: "IX_IpSubnets_Cidr",
                table: "IpSubnets",
                column: "Cidr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IpSubnets_Name",
                table: "IpSubnets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IpObservations");

            migrationBuilder.DropTable(
                name: "IpReservations");

            migrationBuilder.DropTable(
                name: "IpSubnets");
        }
    }
}
