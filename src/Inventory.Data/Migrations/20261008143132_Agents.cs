using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class Agents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentKeyHash",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AgentLastSeenUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentVersion",
                table: "Computers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentKeyHash",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "AgentLastSeenUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "AgentVersion",
                table: "Computers");
        }
    }
}
