using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Grundstuecksfinder.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyTelemetry",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    PageLoads = table.Column<long>(type: "bigint", nullable: false),
                    Searches = table.Column<long>(type: "bigint", nullable: false),
                    TableRowOpens = table.Column<long>(type: "bigint", nullable: false),
                    OutgoingClicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyTelemetry", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "ImportRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false),
                    SkippedParts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Properties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Str = table.Column<string>(type: "text", nullable: true),
                    Hnr = table.Column<string>(type: "text", nullable: true),
                    HnrZus = table.Column<string>(type: "text", nullable: true),
                    Plz = table.Column<string>(type: "text", nullable: true),
                    Ort = table.Column<string>(type: "text", nullable: true),
                    Gemeinde = table.Column<string>(type: "text", nullable: true),
                    FlaecheAmtl = table.Column<double>(type: "double precision", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    ImportRunId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Properties_ImportRuns_ImportRunId",
                        column: x => x.ImportRunId,
                        principalTable: "ImportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceStates",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    ServedRunId = table.Column<int>(type: "integer", nullable: true),
                    LastProbeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastProbeFingerprint = table.Column<string>(type: "text", nullable: true),
                    LastProbeError = table.Column<string>(type: "text", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceStates", x => x.Source);
                    table.ForeignKey(
                        name: "FK_SourceStates_ImportRuns_ServedRunId",
                        column: x => x.ServedRunId,
                        principalTable: "ImportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_Source",
                table: "ImportRuns",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Gemeinde",
                table: "Properties",
                column: "Gemeinde");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ImportRunId",
                table: "Properties",
                column: "ImportRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Plz",
                table: "Properties",
                column: "Plz");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Source",
                table: "Properties",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_SourceStates_ServedRunId",
                table: "SourceStates",
                column: "ServedRunId");

            // Imports stream into this table in short batches and are swapped into Properties in
            // one short transaction at the end, instead of holding a transaction open for the
            // whole (multi-hour) import. UNLOGGED: contents are disposable, so skip the WAL. Not
            // part of the EF model: only PropertyBulkWriter touches it, through raw SQL.
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

            migrationBuilder.DropTable(
                name: "DailyTelemetry");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropTable(
                name: "SourceStates");

            migrationBuilder.DropTable(
                name: "ImportRuns");
        }
    }
}
