using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: true),
                    CooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFiredUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRules_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AlertRuleChannels",
                columns: table => new
                {
                    AlertRuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRuleChannels", x => new { x.AlertRuleId, x.ChannelsId });
                    table.ForeignKey(
                        name: "FK_AlertRuleChannels_AlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertRuleChannels_NotificationChannels_ChannelsId",
                        column: x => x.ChannelsId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRuleChannels_ChannelsId",
                table: "AlertRuleChannels",
                column: "ChannelsId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_CollectionId",
                table: "AlertRules",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_Name",
                table: "AlertRules",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertRuleChannels");

            migrationBuilder.DropTable(
                name: "AlertRules");
        }
    }
}
