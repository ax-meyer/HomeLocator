using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Grundstuecksfinder.Migrations
{
    /// <inheritdoc />
    public partial class StoreAddressCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "Lat",
                table: "Properties",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Lon",
                table: "Properties",
                type: "real",
                nullable: true);

            // The staging table is raw SQL, not part of the EF model (see InitialCreate).
            migrationBuilder.Sql("ALTER TABLE \"PropertyStaging\" ADD COLUMN \"Lat\" real, ADD COLUMN \"Lon\" real");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"PropertyStaging\" DROP COLUMN \"Lat\", DROP COLUMN \"Lon\"");

            migrationBuilder.DropColumn(
                name: "Lat",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Lon",
                table: "Properties");
        }
    }
}
