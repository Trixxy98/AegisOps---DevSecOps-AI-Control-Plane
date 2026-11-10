using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentRuntimeSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_runtime_snapshots",
                schema: "deploy",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    inspection_available = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    containers = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environment_runtime_snapshots", x => x.environment_id);
                    table.ForeignKey(
                        name: "fk_environment_runtime_snapshots_environments_environment_id",
                        column: x => x.environment_id,
                        principalSchema: "org",
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_runtime_snapshots",
                schema: "deploy");
        }
    }
}
