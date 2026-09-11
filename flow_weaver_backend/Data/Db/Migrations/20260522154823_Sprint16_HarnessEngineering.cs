using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Sprint16_HarnessEngineering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastSimulationId",
                table: "workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_scratches",
                columns: table => new
                {
                    AgentScratchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_scratches", x => x.AgentScratchId);
                });

            migrationBuilder.CreateTable(
                name: "plan_features",
                columns: table => new
                {
                    PlanFeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ImportToken = table.Column<string>(type: "text", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SnippetType = table.Column<string>(type: "text", nullable: true),
                    Acceptance = table.Column<string>(type: "jsonb", nullable: false),
                    VerifiedBy = table.Column<string>(type: "text", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_features", x => x.PlanFeatureId);
                    table.CheckConstraint("CK_plan_features_Status", "\"Status\" IN ('pending', 'in_progress', 'verified', 'rejected', 'skipped')");
                });

            migrationBuilder.CreateTable(
                name: "simulation_results",
                columns: table => new
                {
                    SimulationResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    IssueCount = table.Column<int>(type: "integer", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    SchemaHash = table.Column<string>(type: "text", nullable: false),
                    Issues = table.Column<string>(type: "jsonb", nullable: false),
                    Warnings = table.Column<string>(type: "jsonb", nullable: false),
                    SimulatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SimulatedBy = table.Column<string>(type: "text", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_results", x => x.SimulationResultId);
                });

            migrationBuilder.CreateTable(
                name: "workflow_acceptance_tests",
                columns: table => new
                {
                    WorkflowAcceptanceTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Inputs = table.Column<string>(type: "jsonb", nullable: false),
                    Assertions = table.Column<string>(type: "jsonb", nullable: false),
                    LastStatus = table.Column<string>(type: "text", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailures = table.Column<string>(type: "jsonb", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_acceptance_tests", x => x.WorkflowAcceptanceTestId);
                    table.CheckConstraint("CK_workflow_acceptance_tests_LastStatus", "\"LastStatus\" IS NULL OR \"LastStatus\" IN ('passed', 'failed', 'error', 'skipped', 'running')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_scratches_CompanyId",
                table: "agent_scratches",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_scratches_CompanyId_ConversationId_Key",
                table: "agent_scratches",
                columns: new[] { "CompanyId", "ConversationId", "Key" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_agent_scratches_WorkflowPlanId",
                table: "agent_scratches",
                column: "WorkflowPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_CompanyId",
                table: "plan_features",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_CompanyId_Status",
                table: "plan_features",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_ImportToken_Ordinal",
                table: "plan_features",
                columns: new[] { "ImportToken", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_WorkflowPlanId_Ordinal",
                table: "plan_features",
                columns: new[] { "WorkflowPlanId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_simulation_results_CompanyId",
                table: "simulation_results",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_results_WorkflowId_SchemaHash_SimulatedAt",
                table: "simulation_results",
                columns: new[] { "WorkflowId", "SchemaHash", "SimulatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_acceptance_tests_CompanyId",
                table: "workflow_acceptance_tests",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_acceptance_tests_CompanyId_LastStatus",
                table: "workflow_acceptance_tests",
                columns: new[] { "CompanyId", "LastStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_acceptance_tests_WorkflowId_IsActive",
                table: "workflow_acceptance_tests",
                columns: new[] { "WorkflowId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_scratches");

            migrationBuilder.DropTable(
                name: "plan_features");

            migrationBuilder.DropTable(
                name: "simulation_results");

            migrationBuilder.DropTable(
                name: "workflow_acceptance_tests");

            migrationBuilder.DropColumn(
                name: "LastSimulationId",
                table: "workflows");
        }
    }
}
