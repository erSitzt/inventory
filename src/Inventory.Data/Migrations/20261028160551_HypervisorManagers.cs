using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class HypervisorManagers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hosts und VMs gehören ab jetzt zu einer Verwaltung. Der bisherige Stand stammte aus einer Verwaltung je Art; er wird
            // verworfen und beim nächsten Abgleich neu gelesen (die Verwaltungen entstehen beim Start aus den alten Einstellungen).
            migrationBuilder.Sql("UPDATE Computers SET HypervisorHostId = NULL, HostSource = NULL WHERE HypervisorHostId IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM HypervisorVms;");
            migrationBuilder.Sql("DELETE FROM HypervisorHosts;");
            migrationBuilder.DropIndex(
                name: "IX_HypervisorVms_Kind_ExternalId",
                table: "HypervisorVms");

            migrationBuilder.DropIndex(
                name: "IX_HypervisorHosts_Kind_ExternalId",
                table: "HypervisorHosts");

            migrationBuilder.AddColumn<int>(
                name: "ManagerId",
                table: "HypervisorVms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ManagerId",
                table: "HypervisorHosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "HypervisorManagers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<int>(type: "INTEGER", nullable: true),
                    Thumbprint = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HypervisorManagers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HypervisorManagers_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_ManagerId_ExternalId",
                table: "HypervisorVms",
                columns: new[] { "ManagerId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorHosts_ManagerId_ExternalId",
                table: "HypervisorHosts",
                columns: new[] { "ManagerId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorManagers_CredentialId",
                table: "HypervisorManagers",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorManagers_Name",
                table: "HypervisorManagers",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HypervisorHosts_HypervisorManagers_ManagerId",
                table: "HypervisorHosts",
                column: "ManagerId",
                principalTable: "HypervisorManagers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_HypervisorVms_HypervisorManagers_ManagerId",
                table: "HypervisorVms",
                column: "ManagerId",
                principalTable: "HypervisorManagers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HypervisorHosts_HypervisorManagers_ManagerId",
                table: "HypervisorHosts");

            migrationBuilder.DropForeignKey(
                name: "FK_HypervisorVms_HypervisorManagers_ManagerId",
                table: "HypervisorVms");

            migrationBuilder.DropTable(
                name: "HypervisorManagers");

            migrationBuilder.DropIndex(
                name: "IX_HypervisorVms_ManagerId_ExternalId",
                table: "HypervisorVms");

            migrationBuilder.DropIndex(
                name: "IX_HypervisorHosts_ManagerId_ExternalId",
                table: "HypervisorHosts");

            migrationBuilder.DropColumn(
                name: "ManagerId",
                table: "HypervisorVms");

            migrationBuilder.DropColumn(
                name: "ManagerId",
                table: "HypervisorHosts");

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_Kind_ExternalId",
                table: "HypervisorVms",
                columns: new[] { "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorHosts_Kind_ExternalId",
                table: "HypervisorHosts",
                columns: new[] { "Kind", "ExternalId" },
                unique: true);
        }
    }
}
