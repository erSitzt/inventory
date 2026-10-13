using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class TargetJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Interrupted",
                table: "DeploymentTargets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "JobId",
                table: "DeploymentTargets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentTargets_JobId",
                table: "DeploymentTargets",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeploymentTargets_JobId",
                table: "DeploymentTargets");

            migrationBuilder.DropColumn(
                name: "Interrupted",
                table: "DeploymentTargets");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "DeploymentTargets");
        }
    }
}
