using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditRoleSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "AuditEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "AuditEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TimeUtc",
                table: "AuditEntries",
                column: "TimeUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_TimeUtc",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AuditEntries");
        }
    }
}
