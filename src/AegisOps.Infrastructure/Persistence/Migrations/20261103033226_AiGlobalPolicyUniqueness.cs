using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiGlobalPolicyUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_policies_scope_id",
                schema: "ai",
                table: "ai_policies");

            migrationBuilder.DropIndex(
                name: "ix_ai_policies_scope_scope_id",
                schema: "ai",
                table: "ai_policies");

            migrationBuilder.CreateIndex(
                name: "ix_ai_policies_scope",
                schema: "ai",
                table: "ai_policies",
                column: "scope",
                unique: true,
                filter: "scope = 'Global' AND is_enabled = true");

            migrationBuilder.CreateIndex(
                name: "ix_ai_policies_scope_id",
                schema: "ai",
                table: "ai_policies",
                column: "scope_id",
                unique: true,
                filter: "scope = 'Team' AND is_enabled = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_policies_scope",
                schema: "ai",
                table: "ai_policies");

            migrationBuilder.DropIndex(
                name: "ix_ai_policies_scope_id",
                schema: "ai",
                table: "ai_policies");

            migrationBuilder.CreateIndex(
                name: "ix_ai_policies_scope_id",
                schema: "ai",
                table: "ai_policies",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_policies_scope_scope_id",
                schema: "ai",
                table: "ai_policies",
                columns: new[] { "scope", "scope_id" },
                unique: true,
                filter: "is_enabled = true");
        }
    }
}
