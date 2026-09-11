using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class SubflowOutcomeParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StrictestTier",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                table: "step_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tier",
                table: "step_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StrictestTier",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "step_runs");
        }
    }
}
