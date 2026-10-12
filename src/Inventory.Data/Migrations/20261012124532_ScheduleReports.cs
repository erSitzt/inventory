using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NotificationChannelId",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReportId",
                table: "Schedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_NotificationChannelId",
                table: "Schedules",
                column: "NotificationChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_ReportId",
                table: "Schedules",
                column: "ReportId");

            migrationBuilder.AddForeignKey(
                name: "FK_Schedules_NotificationChannels_NotificationChannelId",
                table: "Schedules",
                column: "NotificationChannelId",
                principalTable: "NotificationChannels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Schedules_Reports_ReportId",
                table: "Schedules",
                column: "ReportId",
                principalTable: "Reports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Schedules_NotificationChannels_NotificationChannelId",
                table: "Schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_Schedules_Reports_ReportId",
                table: "Schedules");

            migrationBuilder.DropIndex(
                name: "IX_Schedules_NotificationChannelId",
                table: "Schedules");

            migrationBuilder.DropIndex(
                name: "IX_Schedules_ReportId",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "NotificationChannelId",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "ReportId",
                table: "Schedules");
        }
    }
}
