using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalBoxApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPriorityAndNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Status",
                table: "Orders");

            migrationBuilder.AddColumn<bool>(
                name: "IsPriority",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_IsPriority",
                table: "Orders",
                columns: new[] { "Status", "IsPriority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Status_IsPriority",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsPriority",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");
        }
    }
}
