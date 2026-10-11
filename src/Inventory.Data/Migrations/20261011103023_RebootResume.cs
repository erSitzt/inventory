using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class RebootResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BootTimeUtc",
                table: "DeploymentTargets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RebootUtc",
                table: "DeploymentTargets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResumeFromStep",
                table: "DeploymentTargets",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BootTimeUtc",
                table: "DeploymentTargets");

            migrationBuilder.DropColumn(
                name: "RebootUtc",
                table: "DeploymentTargets");

            migrationBuilder.DropColumn(
                name: "ResumeFromStep",
                table: "DeploymentTargets");
        }
    }
}
