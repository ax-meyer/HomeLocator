using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Grundstuecksfinder.Migrations
{
    /// <inheritdoc />
    public partial class AddImportSkippedTiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkippedTiles",
                table: "ImportLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkippedTiles",
                table: "ImportLogs");
        }
    }
}
