using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_variables",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    protected_value = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    is_secret = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environment_variables", x => x.id);
                    table.ForeignKey(
                        name: "fk_environment_variables_environments_environment_id",
                        column: x => x.environment_id,
                        principalSchema: "org",
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_environment_variables_environment_id_name",
                schema: "org",
                table: "environment_variables",
                columns: new[] { "environment_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_variables",
                schema: "org");
        }
    }
}
