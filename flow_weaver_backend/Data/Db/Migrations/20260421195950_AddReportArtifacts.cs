using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddReportArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_artifacts",
                columns: table => new
                {
                    ReportArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    Format = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    StoredBytes = table.Column<int>(type: "integer", nullable: false),
                    IsCompressed = table.Column<bool>(type: "boolean", nullable: false),
                    ContentBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    AgentConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentName = table.Column<string>(type: "text", nullable: true),
                    AgentPromptEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    AgentPromptRedacted = table.Column<bool>(type: "boolean", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_artifacts", x => x.ReportArtifactId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_AgentConversationId",
                table: "report_artifacts",
                column: "AgentConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId",
                table: "report_artifacts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_CreatedAt",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_Format",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "Format" });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_Source",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_UserId_CreatedAt",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "UserId", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_ExpiresAt",
                table: "report_artifacts",
                column: "ExpiresAt",
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_WorkflowRunId",
                table: "report_artifacts",
                column: "WorkflowRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_artifacts");
        }
    }
}
