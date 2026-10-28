using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class HypervisorKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HypervisorVms_ExternalId",
                table: "HypervisorVms");

            migrationBuilder.DropIndex(
                name: "IX_HypervisorHosts_ExternalId",
                table: "HypervisorHosts");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "HypervisorVms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "HypervisorHosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HypervisorVms_Kind_ExternalId",
                table: "HypervisorVms");

            migrationBuilder.DropIndex(
                name: "IX_HypervisorHosts_Kind_ExternalId",
                table: "HypervisorHosts");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "HypervisorVms");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "HypervisorHosts");

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorVms_ExternalId",
                table: "HypervisorVms",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HypervisorHosts_ExternalId",
                table: "HypervisorHosts",
                column: "ExternalId",
                unique: true);
        }
    }
}
