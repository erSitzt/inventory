using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AskUser",
                table: "Packages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DeferMinutes",
                table: "Packages",
                type: "INTEGER",
                nullable: false,
                // Bestehende Pakete bekommen die Vorgabe, nicht 0 Minuten.
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "MaxDeferrals",
                table: "Packages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "Deferrals",
                table: "DeploymentTargets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NotBeforeUtc",
                table: "DeploymentTargets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskUser",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "DeferMinutes",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "MaxDeferrals",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "Deferrals",
                table: "DeploymentTargets");

            migrationBuilder.DropColumn(
                name: "NotBeforeUtc",
                table: "DeploymentTargets");
        }
    }
}
