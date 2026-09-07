using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalBoxApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInventorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Sku = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SkuRaw = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    OnHand = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryUploads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_Kind_Sku",
                table: "InventoryItems",
                columns: new[] { "Kind", "Sku" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryUploads_Kind",
                table: "InventoryUploads",
                column: "Kind",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryItems");

            migrationBuilder.DropTable(
                name: "InventoryUploads");
        }
    }
}
