using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchDiscovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalsAndInterleaving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Variant",
                table: "SearchEventResults",
                type: "character varying(1)",
                maxLength: 1,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaperSignals",
                columns: table => new
                {
                    PaperId = table.Column<long>(type: "bigint", nullable: false),
                    CitationCount = table.Column<int>(type: "integer", nullable: true),
                    InfluentialCitationCount = table.Column<int>(type: "integer", nullable: true),
                    GitHubStars = table.Column<int>(type: "integer", nullable: true),
                    FetchedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperSignals", x => x.PaperId);
                    table.ForeignKey(
                        name: "FK_PaperSignals_Papers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "Papers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperSignals");

            migrationBuilder.DropColumn(
                name: "Variant",
                table: "SearchEventResults");
        }
    }
}
