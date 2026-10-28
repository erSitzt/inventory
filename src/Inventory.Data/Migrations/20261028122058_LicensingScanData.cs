using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class LicensingScanData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BiosUuid",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClusterName",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CpuCoresPerSocket",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CpuSockets",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HostComputerId",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostSource",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HyperVGuest",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HypervisorPresent",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenseChannel",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LicensingScanUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsEdition",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OsProductType",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OsSku",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedHostName",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HostedVirtualMachines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    BiosUuid = table.Column<string>(type: "TEXT", nullable: true),
                    VirtualCpus = table.Column<int>(type: "INTEGER", nullable: true),
                    Running = table.Column<bool>(type: "INTEGER", nullable: false),
                    GuestComputerId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostedVirtualMachines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostedVirtualMachines_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostedVirtualMachines_Computers_GuestComputerId",
                        column: x => x.GuestComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ServerFeatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeatureId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerFeatures_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SqlInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: true),
                    Edition = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    PatchLevel = table.Column<string>(type: "TEXT", nullable: true),
                    Clustered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SqlInstances_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Computers_BiosUuid",
                table: "Computers",
                column: "BiosUuid");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_HostComputerId",
                table: "Computers",
                column: "HostComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_HostedVirtualMachines_BiosUuid",
                table: "HostedVirtualMachines",
                column: "BiosUuid");

            migrationBuilder.CreateIndex(
                name: "IX_HostedVirtualMachines_ComputerId",
                table: "HostedVirtualMachines",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_HostedVirtualMachines_GuestComputerId",
                table: "HostedVirtualMachines",
                column: "GuestComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerFeatures_ComputerId",
                table: "ServerFeatures",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_SqlInstances_ComputerId",
                table: "SqlInstances",
                column: "ComputerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Computers_Computers_HostComputerId",
                table: "Computers",
                column: "HostComputerId",
                principalTable: "Computers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Computers_Computers_HostComputerId",
                table: "Computers");

            migrationBuilder.DropTable(
                name: "HostedVirtualMachines");

            migrationBuilder.DropTable(
                name: "ServerFeatures");

            migrationBuilder.DropTable(
                name: "SqlInstances");

            migrationBuilder.DropIndex(
                name: "IX_Computers_BiosUuid",
                table: "Computers");

            migrationBuilder.DropIndex(
                name: "IX_Computers_HostComputerId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "BiosUuid",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "ClusterName",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "CpuCoresPerSocket",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "CpuSockets",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "HostComputerId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "HostSource",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "HyperVGuest",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "HypervisorPresent",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LicenseChannel",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LicensingScanUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsEdition",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsProductType",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "OsSku",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "ReportedHostName",
                table: "Computers");
        }
    }
}
