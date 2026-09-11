using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <summary>
    /// Data fix, no schema change. Workflow-generated report artifacts were
    /// persisted with UserId = null even though the producing run records who
    /// triggered it (workflow_runs.CreatedBy holds the user's Guid as text).
    /// ReportHandler now propagates that attribution for new artifacts; this
    /// backfills the existing rows so the admin artifacts view stops showing
    /// an empty user for historical workflow documents.
    ///
    /// Only rows whose run creator parses as a real Guid are touched —
    /// schedule-triggered runs store Guid.Empty and legacy rows may hold
    /// free-form text; both stay unattributed on purpose.
    /// </summary>
    public partial class BackfillWorkflowReportArtifactUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE report_artifacts ra
                SET "UserId" = wr."CreatedBy"::uuid
                FROM workflow_runs wr
                WHERE ra."WorkflowRunId" = wr."WorkflowRunId"
                  AND ra."UserId" IS NULL
                  AND ra."Source" = 'workflow'
                  AND wr."CreatedBy" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                  AND wr."CreatedBy" <> '00000000-0000-0000-0000-000000000000';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data fix: we cannot tell backfilled rows apart from
            // rows attributed at generation time, and reverting attribution
            // would only reintroduce the bug's symptom. Intentionally a no-op.
        }
    }
}
