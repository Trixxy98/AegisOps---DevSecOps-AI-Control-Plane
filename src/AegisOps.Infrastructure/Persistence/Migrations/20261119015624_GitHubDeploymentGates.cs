using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GitHubDeploymentGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_deployment_gates",
                schema: "deploy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_full_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    run_id = table.Column<long>(type: "bigint", nullable: false),
                    environment_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(255)", maxLength: 255, nullable: true),
                    callback_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    run_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    callback_status_code = table.Column<int>(type: "integer", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_deployment_gates", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_deployment_gates_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalSchema: "deploy",
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_github_deployment_gates_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "org",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_github_deployment_gates_delivery_id",
                schema: "deploy",
                table: "github_deployment_gates",
                column: "delivery_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_deployment_gates_deployment_id",
                schema: "deploy",
                table: "github_deployment_gates",
                column: "deployment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_deployment_gates_project_id",
                schema: "deploy",
                table: "github_deployment_gates",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_deployment_gates",
                schema: "deploy");
        }
    }
}
