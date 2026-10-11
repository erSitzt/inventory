using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RespectMaintenanceWindow",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RespectMaintenanceWindow",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RetryOffline",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Bisher stand die Frist allein dafür, dass Offline-Rechner nachgeholt werden.
            migrationBuilder.Sql("UPDATE Deployments SET RetryOffline = 1 WHERE RetryUntilUtc IS NOT NULL");

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceWindow",
                table: "Collections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RespectMaintenanceWindow",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "RespectMaintenanceWindow",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RetryOffline",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "MaintenanceWindow",
                table: "Collections");
        }
    }
}
