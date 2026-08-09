using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Grundstuecksfinder.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceDiscriminator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Properties",
                type: "text",
                nullable: false,
                defaultValue: "nrw");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "ImportLogs",
                type: "text",
                nullable: false,
                defaultValue: "nrw");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Source",
                table: "Properties",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_ImportLogs_Source_DatasetName_FileName_FileTimestamp",
                table: "ImportLogs",
                columns: new[] { "Source", "DatasetName", "FileName", "FileTimestamp" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Properties_Source",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_ImportLogs_Source_DatasetName_FileName_FileTimestamp",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ImportLogs");
        }
    }
}
