using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamWebhookEventSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_types",
                schema: "org",
                table: "team_webhook_settings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "*");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "event_types",
                schema: "org",
                table: "team_webhook_settings");
        }
    }
}
