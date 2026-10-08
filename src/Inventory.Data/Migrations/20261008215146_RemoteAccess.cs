using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoteAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnyDeskId",
                table: "Computers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionId",
                table: "Computers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamViewerId",
                table: "Computers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnyDeskId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "TeamViewerId",
                table: "Computers");
        }
    }
}
