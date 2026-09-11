using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddGitWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "git_webhook_deliveries",
                columns: table => new
                {
                    GitWebhookDeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitWebhookId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: true),
                    Branch = table.Column<string>(type: "text", nullable: true),
                    CommitSha = table.Column<string>(type: "text", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_git_webhook_deliveries", x => x.GitWebhookDeliveryId);
                });

            migrationBuilder.CreateTable(
                name: "git_webhooks",
                columns: table => new
                {
                    GitWebhookId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitRepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    EncryptedSecret = table.Column<byte[]>(type: "bytea", nullable: true),
                    OnPushWorkflowId = table.Column<Guid>(type: "uuid", nullable: true),
                    OnPushBranches = table.Column<List<string>>(type: "text[]", nullable: false),
                    AutoPull = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastDeliveryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDeliveryStatus = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_git_webhooks", x => x.GitWebhookId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_git_webhook_deliveries_CompanyId",
                table: "git_webhook_deliveries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_webhook_deliveries_GitWebhookId_At",
                table: "git_webhook_deliveries",
                columns: new[] { "GitWebhookId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_git_webhooks_CompanyId",
                table: "git_webhooks",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_webhooks_GitRepositoryId",
                table: "git_webhooks",
                column: "GitRepositoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "git_webhook_deliveries");

            migrationBuilder.DropTable(
                name: "git_webhooks");
        }
    }
}
