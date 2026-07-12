using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchDiscovery.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Data-only: the single Admin tier split into Admin (people ops) and
    /// Owner (everything). Every pre-split Admin was the operator, so they
    /// all become Owners.
    /// </summary>
    public partial class PromoteAdminsToOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """UPDATE "AppUsers" SET "Role" = 'Owner' WHERE "Role" = 'Admin';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """UPDATE "AppUsers" SET "Role" = 'Admin' WHERE "Role" = 'Owner';""");
        }
    }
}
