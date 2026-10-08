using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "security");

            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    image_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    image_digest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    ci_provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ci_run_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ci_run_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    build_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    test_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    test_summary = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifacts", x => x.id);
                    table.CheckConstraint("ck_artifacts_build_status", "build_status IN ('Unknown', 'Passed', 'Failed', 'Skipped')");
                    table.CheckConstraint("ck_artifacts_test_status", "test_status IN ('Unknown', 'Passed', 'Failed', 'Skipped')");
                    table.ForeignKey(
                        name: "fk_artifacts_api_keys_created_by_api_key_id",
                        column: x => x.created_by_api_key_id,
                        principalSchema: "identity",
                        principalTable: "api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_artifacts_asp_net_users_created_by_id",
                        column: x => x.created_by_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_artifacts_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "org",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_created_by_api_key_id",
                schema: "security",
                table: "artifacts",
                column: "created_by_api_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_created_by_id",
                schema: "security",
                table: "artifacts",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_project_id_created_at",
                schema: "security",
                table: "artifacts",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_project_id_version",
                schema: "security",
                table: "artifacts",
                columns: new[] { "project_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "security");
        }
    }
}
