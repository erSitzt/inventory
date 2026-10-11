using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CatchUpMissed",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                // Bisher wurden verpasste Läufe immer nachgeholt: bestehende Zeitpläne behalten das.
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndLocal",
                table: "Schedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonthDay",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MonthOffsetDays",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MonthWeek",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MonthWeekday",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecheckDays",
                table: "Schedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleId",
                table: "Deployments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_ScheduleId",
                table: "Deployments",
                column: "ScheduleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_Schedules_ScheduleId",
                table: "Deployments",
                column: "ScheduleId",
                principalTable: "Schedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_Schedules_ScheduleId",
                table: "Deployments");

            migrationBuilder.DropIndex(
                name: "IX_Deployments_ScheduleId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "CatchUpMissed",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "EndLocal",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MonthDay",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MonthOffsetDays",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MonthWeek",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "MonthWeekday",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "RecheckDays",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "Deployments");
        }
    }
}
