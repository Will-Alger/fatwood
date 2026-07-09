using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchDiscovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Personalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeUrl",
                table: "Papers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProfileVersion",
                table: "AnalysisResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LlmStepConfigs",
                columns: table => new
                {
                    Step = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmStepConfigs", x => x.Step);
                });

            migrationBuilder.CreateTable(
                name: "PaperEmbeddings",
                columns: table => new
                {
                    PaperId = table.Column<long>(type: "bigint", nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Vector = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperEmbeddings", x => x.PaperId);
                    table.ForeignKey(
                        name: "FK_PaperEmbeddings_Papers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "Papers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ExperienceSummary = table.Column<string>(type: "text", nullable: false),
                    Goals = table.Column<string>(type: "text", nullable: false),
                    WeeklyHours = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmStepConfigs");

            migrationBuilder.DropTable(
                name: "PaperEmbeddings");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "CodeUrl",
                table: "Papers");

            migrationBuilder.DropColumn(
                name: "ProfileVersion",
                table: "AnalysisResults");
        }
    }
}
