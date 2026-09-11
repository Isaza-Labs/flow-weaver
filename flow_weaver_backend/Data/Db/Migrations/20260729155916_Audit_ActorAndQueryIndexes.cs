using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Audit_ActorAndQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_EntityType",
                table: "audit_logs");

            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "audit_logs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_auth_events_At",
                table: "auth_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_EntityId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "EntityId", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_EntityType_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "EntityType", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_UserId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "UserId", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_RequestId",
                table: "audit_logs",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_auth_events_At",
                table: "auth_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_EntityId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_EntityType_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_UserId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_RequestId",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "Actor",
                table: "audit_logs");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityType",
                table: "audit_logs",
                column: "EntityType");
        }
    }
}
