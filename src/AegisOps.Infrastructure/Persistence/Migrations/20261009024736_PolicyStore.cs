using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "policy");

            migrationBuilder.CreateTable(
                name: "policies",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    applies_to_tiers = table.Column<string[]>(type: "text[]", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policies", x => x.id);
                    table.CheckConstraint("ck_policies_scope", "scope IN ('Global', 'Team', 'Project')");
                    table.ForeignKey(
                        name: "fk_policies_asp_net_users_created_by_id",
                        column: x => x.created_by_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_policies_asp_net_users_updated_by_id",
                        column: x => x.updated_by_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_rules",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    effect = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_rules", x => x.id);
                    table.CheckConstraint("ck_policy_rules_effect", "effect IN ('Deny', 'RequireApproval', 'Warn')");
                    table.CheckConstraint("ck_policy_rules_type", "type IN ('RequireTestsPassed', 'RequireScan', 'MaxFindings', 'RequireApprovals', 'AllowedBranches', 'DeploymentWindow', 'RequireImageDigest', 'RequirePriorEnvironment')");
                    table.ForeignKey(
                        name: "fk_policy_rules_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "policy",
                        principalTable: "policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_policies_created_by_id",
                schema: "policy",
                table: "policies",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_policies_scope_scope_id",
                schema: "policy",
                table: "policies",
                columns: new[] { "scope", "scope_id" },
                filter: "archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_policies_updated_by_id",
                schema: "policy",
                table: "policies",
                column: "updated_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_policy_rules_policy_id_order",
                schema: "policy",
                table: "policy_rules",
                columns: new[] { "policy_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policy_rules",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "policies",
                schema: "policy");
        }
    }
}
