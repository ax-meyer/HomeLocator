using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Grundstuecksfinder.Migrations
{
    /// <inheritdoc />
    public partial class AddImportCompletionAndStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompletedAt",
                table: "ImportLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ImportLogs",
                type: "text",
                nullable: true);

            // Every import so far that wrote rows committed; empty ones may have failed midway
            // and should be retried.
            migrationBuilder.Sql(
                "UPDATE \"ImportLogs\" SET \"CompletedAt\" = \"ImportedAt\" WHERE \"RecordCount\" > 0");

            // Imports stream into this table in short batches and are swapped into Properties in
            // one short transaction at the end, instead of holding a transaction open for the
            // whole (multi-hour) import. UNLOGGED: contents are disposable, so skip the WAL.
            migrationBuilder.Sql("""
                CREATE UNLOGGED TABLE "PropertyStaging" (
                    "Str" text,
                    "Hnr" text,
                    "HnrZus" text,
                    "Plz" text,
                    "Ort" text,
                    "Gemeinde" text,
                    "FlaecheAmtl" double precision,
                    "Source" text NOT NULL
                );
                CREATE INDEX "IX_PropertyStaging_Source" ON "PropertyStaging" ("Source");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE \"PropertyStaging\"");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ImportLogs");
        }
    }
}
