using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxFailurePercent",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFailures",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxParallel",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFailurePercent",
                table: "Deployments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFailures",
                table: "Deployments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxParallel",
                table: "Deployments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                table: "Deployments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxFailurePercent",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MaxFailures",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MaxParallel",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MaxFailurePercent",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "MaxFailures",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "MaxParallel",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "StopReason",
                table: "Deployments");
        }
    }
}
