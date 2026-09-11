using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowTriggerTargetDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add NOT NULL with a transient empty-array default so existing
            // trigger rows backfill cleanly, then drop the default — the model
            // declares none and the app always supplies the value.
            migrationBuilder.AddColumn<List<Guid>>(
                name: "TargetDevices",
                table: "workflow_triggers",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.Sql(
                "ALTER TABLE workflow_triggers ALTER COLUMN \"TargetDevices\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetDevices",
                table: "workflow_triggers");
        }
    }
}
