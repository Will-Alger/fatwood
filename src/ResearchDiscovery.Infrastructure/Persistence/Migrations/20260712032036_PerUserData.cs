using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ResearchDiscovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerUserData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Bookmarks",
                table: "Bookmarks");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisResults_PaperId",
                table: "AnalysisResults");

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "UserProfiles",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            // Converting an existing column to identity starts its sequence at
            // 1, but the legacy singleton profile row already holds Id 1 — the
            // first insert would collide. Advance the sequence past MAX(Id).
            migrationBuilder.Sql(
                """
                SELECT setval(
                    pg_get_serial_sequence('"UserProfiles"', 'Id'),
                    COALESCE((SELECT MAX("Id") FROM "UserProfiles"), 0) + 1,
                    false);
                """);

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "UserProfiles",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "SearchEvents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "InteractionEvents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "Bookmarks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "Bookmarks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "AnalysisResults",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Bookmarks",
                table: "Bookmarks",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchEvents_UserId",
                table: "SearchEvents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_InteractionEvents_UserId",
                table: "InteractionEvents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_PaperId",
                table: "Bookmarks",
                column: "PaperId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_UserId_PaperId",
                table: "Bookmarks",
                columns: new[] { "UserId", "PaperId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_PaperId",
                table: "AnalysisResults",
                column: "PaperId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_UserId_PaperId",
                table: "AnalysisResults",
                columns: new[] { "UserId", "PaperId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisResults_AppUsers_UserId",
                table: "AnalysisResults",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Bookmarks_AppUsers_UserId",
                table: "Bookmarks",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserProfiles_AppUsers_UserId",
                table: "UserProfiles",
                column: "UserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisResults_AppUsers_UserId",
                table: "AnalysisResults");

            migrationBuilder.DropForeignKey(
                name: "FK_Bookmarks_AppUsers_UserId",
                table: "Bookmarks");

            migrationBuilder.DropForeignKey(
                name: "FK_UserProfiles_AppUsers_UserId",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles");

            migrationBuilder.DropIndex(
                name: "IX_SearchEvents_UserId",
                table: "SearchEvents");

            migrationBuilder.DropIndex(
                name: "IX_InteractionEvents_UserId",
                table: "InteractionEvents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Bookmarks",
                table: "Bookmarks");

            migrationBuilder.DropIndex(
                name: "IX_Bookmarks_PaperId",
                table: "Bookmarks");

            migrationBuilder.DropIndex(
                name: "IX_Bookmarks_UserId_PaperId",
                table: "Bookmarks");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisResults_PaperId",
                table: "AnalysisResults");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisResults_UserId_PaperId",
                table: "AnalysisResults");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "SearchEvents");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "InteractionEvents");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "Bookmarks");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Bookmarks");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AnalysisResults");

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "UserProfiles",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Bookmarks",
                table: "Bookmarks",
                column: "PaperId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_PaperId",
                table: "AnalysisResults",
                column: "PaperId",
                unique: true);
        }
    }
}
