using Microsoft.EntityFrameworkCore.Migrations;

namespace AegisOps.Infrastructure.Persistence.Migrations;

public partial class TeamWebhookSettings : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "team_webhook_settings",
            schema: "org",
            columns: table => new {
                team_id = table.Column<Guid>(type: "uuid", nullable: false),
                url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                encrypted_secret = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => {
                table.PrimaryKey("pk_team_webhook_settings", row => row.team_id);
                table.ForeignKey(
                    name: "fk_team_webhook_settings_teams_team_id",
                    column: row => row.team_id,
                    principalSchema: "org",
                    principalTable: "teams",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "team_webhook_settings", schema: "org");
}
