using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CatalogAutoUpdate",
                table: "Packages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CatalogCollectionId",
                table: "Packages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogId",
                table: "Packages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Packages_CatalogCollectionId",
                table: "Packages",
                column: "CatalogCollectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Packages_Collections_CatalogCollectionId",
                table: "Packages",
                column: "CatalogCollectionId",
                principalTable: "Collections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Packages_Collections_CatalogCollectionId",
                table: "Packages");

            migrationBuilder.DropIndex(
                name: "IX_Packages_CatalogCollectionId",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "CatalogAutoUpdate",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "CatalogCollectionId",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "CatalogId",
                table: "Packages");
        }
    }
}
