using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisOps.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "finding_suppressions",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finding_suppressions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "security_scans",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scanner = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    report_format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tool_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_report_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_scans", x => x.id);
                    table.CheckConstraint("ck_security_scans_format", "report_format IN ('Sarif', 'TrivyJson', 'GitleaksJson', 'SemgrepJson')");
                    table.CheckConstraint("ck_security_scans_scanner", "scanner IN ('Gitleaks', 'Trivy', 'Semgrep')");
                    table.CheckConstraint("ck_security_scans_source", "source IN ('CiUploaded', 'WorkerExecuted')");
                    table.CheckConstraint("ck_security_scans_status", "status IN ('Queued', 'Running', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "fk_security_scans_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalSchema: "security",
                        principalTable: "artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_findings",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    start_line = table.Column<int>(type: "integer", nullable: true),
                    package_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    installed_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    fixed_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cve = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    suppressed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suppression_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    help_uri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_findings", x => x.id);
                    table.CheckConstraint("ck_security_findings_severity", "severity IN ('Unknown', 'Info', 'Low', 'Medium', 'High', 'Critical')");
                    table.CheckConstraint("ck_security_findings_status", "status IN ('Open', 'Suppressed', 'Resolved')");
                    table.ForeignKey(
                        name: "fk_security_findings_security_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "security",
                        principalTable: "security_scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_finding_suppressions_project_id_fingerprint",
                schema: "security",
                table: "finding_suppressions",
                columns: new[] { "project_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_findings_artifact_id_status",
                schema: "security",
                table: "security_findings",
                columns: new[] { "artifact_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_security_findings_fingerprint",
                schema: "security",
                table: "security_findings",
                column: "fingerprint");

            migrationBuilder.CreateIndex(
                name: "ix_security_findings_scan_id_severity",
                schema: "security",
                table: "security_findings",
                columns: new[] { "scan_id", "severity" });

            migrationBuilder.CreateIndex(
                name: "ix_security_scans_artifact_id_scanner",
                schema: "security",
                table: "security_scans",
                columns: new[] { "artifact_id", "scanner" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finding_suppressions",
                schema: "security");

            migrationBuilder.DropTable(
                name: "security_findings",
                schema: "security");

            migrationBuilder.DropTable(
                name: "security_scans",
                schema: "security");
        }
    }
}
