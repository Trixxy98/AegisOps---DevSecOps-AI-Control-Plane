using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebhookDeliveryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_delivery_attempts",
                schema: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_delivery_attempts_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "jobs",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_attempts_job_id_attempt_number",
                schema: "jobs",
                table: "webhook_delivery_attempts",
                columns: new[] { "job_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_attempts_started_at",
                schema: "jobs",
                table: "webhook_delivery_attempts",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_delivery_attempts",
                schema: "jobs");
        }
    }
}
