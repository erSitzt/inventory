using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityPosture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastLogonUtc",
                table: "LocalUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PasswordAgeDays",
                table: "LocalUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BuiltinAdminEnabled",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BuiltinAdminPasswordAgeDays",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CredentialGuard",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LsaProtection",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MemoryIntegrity",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RdpEnabled",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RdpNla",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Smb1Enabled",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UacEnabled",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UacLevel",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WindowsActivated",
                table: "Computers",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastLogonUtc",
                table: "LocalUsers");

            migrationBuilder.DropColumn(
                name: "PasswordAgeDays",
                table: "LocalUsers");

            migrationBuilder.DropColumn(
                name: "BuiltinAdminEnabled",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "BuiltinAdminPasswordAgeDays",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "CredentialGuard",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LsaProtection",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MemoryIntegrity",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "RdpEnabled",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "RdpNla",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "Smb1Enabled",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UacEnabled",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "UacLevel",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "WindowsActivated",
                table: "Computers");
        }
    }
}
