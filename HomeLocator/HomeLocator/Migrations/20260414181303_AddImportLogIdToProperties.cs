using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeLocator.Migrations
{
    /// <inheritdoc />
    public partial class AddImportLogIdToProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImportLogId",
                table: "Properties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ImportLogId",
                table: "Properties",
                column: "ImportLogId");

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_ImportLogs_ImportLogId",
                table: "Properties",
                column: "ImportLogId",
                principalTable: "ImportLogs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Properties_ImportLogs_ImportLogId",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Properties_ImportLogId",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ImportLogId",
                table: "Properties");
        }
    }
}
