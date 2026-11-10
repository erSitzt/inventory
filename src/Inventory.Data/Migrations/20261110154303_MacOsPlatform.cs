using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class MacOsPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MacAutoUpdateCheck",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MacFirewall",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MacFirewallStealth",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MacGatekeeper",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MacSip",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Platform",
                table: "Computers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MacAutoUpdateCheck",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MacFirewall",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MacFirewallStealth",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MacGatekeeper",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MacSip",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "Computers");
        }
    }
}
