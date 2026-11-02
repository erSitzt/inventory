using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiCreatedBy",
                table: "Packages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeploymentRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    ComputerIds = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    ScanAfter = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryOffline = table.Column<bool>(type: "INTEGER", nullable: false),
                    RespectWindow = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    DeploymentId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentRequests_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRequests_PackageId",
                table: "DeploymentRequests",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRequests_Status",
                table: "DeploymentRequests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentRequests");

            migrationBuilder.DropColumn(
                name: "AiCreatedBy",
                table: "Packages");
        }
    }
}
