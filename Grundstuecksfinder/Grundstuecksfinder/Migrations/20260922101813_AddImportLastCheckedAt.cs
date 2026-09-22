using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Grundstuecksfinder.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLastCheckedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastCheckedAt",
                table: "ImportLogs",
                type: "bigint",
                nullable: true);

            // A completed import was current when it completed; the next check moves it forward.
            migrationBuilder.Sql(
                "UPDATE \"ImportLogs\" SET \"LastCheckedAt\" = \"CompletedAt\" WHERE \"CompletedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "ImportLogs");
        }
    }
}
