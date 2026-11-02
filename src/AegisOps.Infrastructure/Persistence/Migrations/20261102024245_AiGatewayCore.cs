using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiGatewayCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "ai_models",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    display_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    max_context_tokens = table.Column<int>(type: "integer", nullable: false),
                    capabilities_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_models", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_policies",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    scope = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allowed_model_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    requests_per_minute = table.Column<int>(type: "integer", nullable: false),
                    requests_per_day = table.Column<int>(type: "integer", nullable: false),
                    max_prompt_chars = table.Column<int>(type: "integer", nullable: false),
                    sensitive_data_action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    blocked_patterns_json = table.Column<string>(type: "jsonb", nullable: false),
                    redact_pii = table.Column<bool>(type: "boolean", nullable: false),
                    store_content = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_policies_asp_net_users_updated_by_id",
                        column: x => x.updated_by_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_policies_teams_scope_id",
                        column: x => x.scope_id,
                        principalSchema: "org",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_requests",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_chars = table.Column<int>(type: "integer", nullable: false),
                    response_chars = table.Column<int>(type: "integer", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    detected_sensitive_types_json = table.Column<string>(type: "jsonb", nullable: false),
                    redactions_applied = table.Column<int>(type: "integer", nullable: false),
                    block_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prompt_preview = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    response_preview = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    prompt_content = table.Column<string>(type: "text", nullable: true),
                    response_content = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_requests_ai_models_ai_model_id",
                        column: x => x.ai_model_id,
                        principalSchema: "ai",
                        principalTable: "ai_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_requests_ai_policies_ai_policy_id",
                        column: x => x.ai_policy_id,
                        principalSchema: "ai",
                        principalTable: "ai_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_requests_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_requests_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "org",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_models_name",
                schema: "ai",
                table: "ai_models",
                column: "name",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_ai_policies_updated_by_id",
                schema: "ai",
                table: "ai_policies",
                column: "updated_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_ai_model_id",
                schema: "ai",
                table: "ai_requests",
                column: "ai_model_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_ai_policy_id",
                schema: "ai",
                table: "ai_requests",
                column: "ai_policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_created_at",
                schema: "ai",
                table: "ai_requests",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_team_id",
                schema: "ai",
                table: "ai_requests",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_user_id_created_at",
                schema: "ai",
                table: "ai_requests",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_requests",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "ai_models",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "ai_policies",
                schema: "ai");
        }
    }
}
