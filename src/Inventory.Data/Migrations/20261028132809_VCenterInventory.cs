using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class VCenterInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HypervisorHostId",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HypervisorHosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Cluster = table.Column<string>(type: "TEXT", nullable: true),
                    CpuSockets = table.Column<int>(type: "INTEGER", nullable: true),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Connected = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HypervisorHosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HypervisorVms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    BiosUuid = table.Column<string>(type: "TEXT", nullable: true),
                    VirtualCpus = table.Column<int>(type: "INTEGER", nullable: true),
                    HostId = table.Column<int>(type: "INTEGER", nullable: true),
                    PoweredOn = table.Column<bool>(type: "INTEGER", nullable: false),
                    GuestHostName = table.Column<string>(type: "TEXT", nullable: true),
                    GuestOs = table.Column<string>(type: "TEXT", nullable: true),
                    GuestComputerId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HypervisorVms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HypervisorVms_Computers_GuestComputerId",
                        column: x => x.GuestComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HypervisorVms_HypervisorHosts_HostId",
                        column: x => x.HostId,
                        principalTable: "HypervisorHosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Computers_HypervisorHostId",
                table: "Computers",
                column: "HypervisorHostId");

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorHosts_ExternalId",
                table: "HypervisorHosts",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_BiosUuid",
                table: "HypervisorVms",
                column: "BiosUuid");

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_ExternalId",
                table: "HypervisorVms",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_GuestComputerId",
                table: "HypervisorVms",
                column: "GuestComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_HostId",
                table: "HypervisorVms",
                column: "HostId");

            migrationBuilder.AddForeignKey(
                name: "FK_Computers_HypervisorHosts_HypervisorHostId",
                table: "Computers",
                column: "HypervisorHostId",
                principalTable: "HypervisorHosts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Computers_HypervisorHosts_HypervisorHostId",
                table: "Computers");

            migrationBuilder.DropTable(
                name: "HypervisorVms");

            migrationBuilder.DropTable(
                name: "HypervisorHosts");

            migrationBuilder.DropIndex(
                name: "IX_Computers_HypervisorHostId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "HypervisorHostId",
                table: "Computers");
        }
    }
}
