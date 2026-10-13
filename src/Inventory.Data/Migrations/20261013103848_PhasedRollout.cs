using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhasedRollout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PilotCollectionId",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PilotHoldHours",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "PilotMaxFailurePercent",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Pilot",
                table: "DeploymentTargets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PilotCollectionId",
                table: "Deployments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PilotHoldHours",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PilotMaxFailurePercent",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PilotReleasedUtc",
                table: "Deployments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PilotCollectionId",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "PilotHoldHours",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "PilotMaxFailurePercent",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "Pilot",
                table: "DeploymentTargets");

            migrationBuilder.DropColumn(
                name: "PilotCollectionId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "PilotHoldHours",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "PilotMaxFailurePercent",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "PilotReleasedUtc",
                table: "Deployments");
        }
    }
}
