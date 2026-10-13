using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class WakeOnLan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WakeTargets",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SubnetMasks",
                table: "NetworkAdapters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WakeTargets",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WakeMac",
                table: "Computers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WakeTargets",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "SubnetMasks",
                table: "NetworkAdapters");

            migrationBuilder.DropColumn(
                name: "WakeTargets",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "WakeMac",
                table: "Computers");
        }
    }
}
