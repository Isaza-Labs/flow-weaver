using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Sprint9_TraceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trace_events",
                columns: table => new
                {
                    TraceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trace_events", x => x.TraceEventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_Action_At",
                table: "trace_events",
                columns: new[] { "Action", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_CompanyId",
                table: "trace_events",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_CompanyId_At",
                table: "trace_events",
                columns: new[] { "CompanyId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_RequestId",
                table: "trace_events",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_UserId_At",
                table: "trace_events",
                columns: new[] { "UserId", "At" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trace_events");
        }
    }
}
