using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchDiscovery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuantizedEmbeddingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "Int8Scale",
                table: "PaperEmbeddings",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "VectorInt8",
                table: "PaperEmbeddings",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Int8Scale",
                table: "PaperEmbeddings");

            migrationBuilder.DropColumn(
                name: "VectorInt8",
                table: "PaperEmbeddings");
        }
    }
}
