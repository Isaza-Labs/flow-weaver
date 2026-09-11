using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    McpServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Transport = table.Column<string>(type: "text", nullable: false),
                    AuthType = table.Column<string>(type: "text", nullable: false),
                    AuthConfigEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    Headers = table.Column<string>(type: "jsonb", nullable: false),
                    TLSSkipVerify = table.Column<bool>(type: "boolean", nullable: false),
                    AllowPrivateNetwork = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProtocolVersion = table.Column<string>(type: "text", nullable: true),
                    LastToolsSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_servers", x => x.McpServerId);
                });

            migrationBuilder.CreateTable(
                name: "mcp_tools",
                columns: table => new
                {
                    McpToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    McpServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    InputSchema = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_tools", x => x.McpToolId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_CompanyId",
                table: "mcp_servers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_CompanyId_Enabled",
                table: "mcp_servers",
                columns: new[] { "CompanyId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_CompanyId",
                table: "mcp_tools",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_CompanyId_McpServerId",
                table: "mcp_tools",
                columns: new[] { "CompanyId", "McpServerId" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_CompanyId_McpServerId_Name",
                table: "mcp_tools",
                columns: new[] { "CompanyId", "McpServerId", "Name" },
                unique: true,
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_servers");

            migrationBuilder.DropTable(
                name: "mcp_tools");
        }
    }
}
