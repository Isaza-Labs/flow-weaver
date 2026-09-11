using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Sprint_SecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // jsonb default must be valid JSON. Existing rows get JSON null,
            // which the executor treats as "no snapshot" and falls back to
            // reading the live workflow — preserving MVP behavior for runs
            // created before this migration.
            migrationBuilder.AddColumn<string>(
                name: "EdgesSnapshot",
                table: "workflow_runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'null'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "NodesSnapshot",
                table: "workflow_runs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'null'::jsonb");

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_LeaseExpiresAt",
                table: "jobs",
                columns: new[] { "Status", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_Status_LeaseExpiresAt",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "EdgesSnapshot",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "NodesSnapshot",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "jobs");
        }
    }
}
