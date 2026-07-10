using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ResearchDiscovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InteractionEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaperId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SearchEventId = table.Column<long>(type: "bigint", nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteractionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InteractionEvents_Papers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "Papers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SearchEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    QueryText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    TotalCandidates = table.Column<int>(type: "integer", nullable: false),
                    ResultLimit = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchEventResults",
                columns: table => new
                {
                    SearchEventId = table.Column<long>(type: "bigint", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    PaperId = table.Column<long>(type: "bigint", nullable: false),
                    Score = table.Column<float>(type: "real", nullable: false),
                    IsWildcard = table.Column<bool>(type: "boolean", nullable: false),
                    Proximity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchEventResults", x => new { x.SearchEventId, x.Rank });
                    table.ForeignKey(
                        name: "FK_SearchEventResults_Papers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "Papers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SearchEventResults_SearchEvents_SearchEventId",
                        column: x => x.SearchEventId,
                        principalTable: "SearchEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InteractionEvents_CreatedUtc",
                table: "InteractionEvents",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InteractionEvents_PaperId",
                table: "InteractionEvents",
                column: "PaperId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchEventResults_PaperId",
                table: "SearchEventResults",
                column: "PaperId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchEvents_CreatedUtc",
                table: "SearchEvents",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InteractionEvents");

            migrationBuilder.DropTable(
                name: "SearchEventResults");

            migrationBuilder.DropTable(
                name: "SearchEvents");
        }
    }
}
