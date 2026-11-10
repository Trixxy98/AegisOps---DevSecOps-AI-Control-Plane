using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamWebhookDeliveryStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_webhook_delivery_stats",
                schema: "org",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false),
                    failed_attempts = table.Column<long>(type: "bigint", nullable: false),
                    dead_lettered = table.Column<long>(type: "bigint", nullable: false),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_webhook_delivery_stats", x => x.team_id);
                    table.ForeignKey(
                        name: "fk_team_webhook_delivery_stats_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "org",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_webhook_delivery_stats",
                schema: "org");
        }
    }
}
