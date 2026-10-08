using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AntivirusProducts",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DefenderEnabled",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DefenderRealTime",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DefenderSignatureUpdatedUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefenderSignatureVersion",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DefenderTamperProtected",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FirewallDomain",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FirewallPrivate",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FirewallPublic",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdateLastInstallUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdateLastSearchUtc",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatePending",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdatePendingCount",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpdateRebootRequired",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateServer",
                table: "Computers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AntivirusProducts",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderEnabled",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderRealTime",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderSignatureUpdatedUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderSignatureVersion",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "DefenderTamperProtected",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "FirewallDomain",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "FirewallPrivate",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "FirewallPublic",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UpdateLastInstallUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UpdateLastSearchUtc",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UpdatePending",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UpdatePendingCount",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UpdateRebootRequired",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UpdateServer",
                table: "Computers");
        }
    }
}
