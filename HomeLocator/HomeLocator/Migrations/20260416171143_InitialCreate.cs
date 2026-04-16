using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HomeLocator.Migrations
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
                name: "ImportLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportedAt = table.Column<long>(type: "bigint", nullable: false),
                    DatasetName = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    FileTimestamp = table.Column<string>(type: "text", nullable: false),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportLogs", x => x.Id);
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
                    ImportLogId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Properties_ImportLogs_ImportLogId",
                        column: x => x.ImportLogId,
                        principalTable: "ImportLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Gemeinde",
                table: "Properties",
                column: "Gemeinde");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ImportLogId",
                table: "Properties",
                column: "ImportLogId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_Plz",
                table: "Properties",
                column: "Plz");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyTelemetry");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropTable(
                name: "ImportLogs");
        }
    }
}
