using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AegisOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AegisOpsDbContext))]
[Migration("20261024120000_JobLeaseToken")]
public sealed class JobLeaseToken : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<Guid>(name: "lease_token", table: "jobs", schema: "jobs", type: "uuid", nullable: true);
        // This property predates the lease but was omitted from the previous migration.
        migrationBuilder.AddColumn<Guid>(name: "previous_artifact_id", table: "deployments", schema: "deploy", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex(name: "ix_deployments_previous_artifact_id", table: "deployments", schema: "deploy", column: "previous_artifact_id");
        migrationBuilder.AddForeignKey(name: "fk_deployments_artifacts_previous_artifact_id", table: "deployments", schema: "deploy",
            column: "previous_artifact_id", principalTable: "artifacts", principalSchema: "security", principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropForeignKey(name: "fk_deployments_artifacts_previous_artifact_id", table: "deployments", schema: "deploy");
        migrationBuilder.DropIndex(name: "ix_deployments_previous_artifact_id", table: "deployments", schema: "deploy");
        migrationBuilder.DropColumn(name: "previous_artifact_id", table: "deployments", schema: "deploy");
        migrationBuilder.DropColumn(name: "lease_token", table: "jobs", schema: "jobs");
    }
}
