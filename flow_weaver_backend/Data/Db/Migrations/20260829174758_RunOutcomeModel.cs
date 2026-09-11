using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class RunOutcomeModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChangedCount",
                table: "workflow_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalState",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RollbackPlanJson",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaHash",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChangedState",
                table: "step_runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChangesState",
                table: "snippets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangedCount",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "FinalState",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "RollbackPlanJson",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "SchemaHash",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "ChangedState",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "ChangesState",
                table: "snippets");
        }
    }
}
