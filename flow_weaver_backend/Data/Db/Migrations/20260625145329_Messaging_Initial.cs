using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Messaging_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalThreadId",
                table: "ai_conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MessagingChannelId",
                table: "ai_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "ai_conversations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "messaging_channels",
                columns: table => new
                {
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    EncryptedBotToken = table.Column<byte[]>(type: "bytea", nullable: true),
                    EncryptedSigningSecret = table.Column<byte[]>(type: "bytea", nullable: true),
                    ExternalConfig = table.Column<string>(type: "jsonb", nullable: false),
                    DefaultAgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaxRole = table.Column<string>(type: "text", nullable: true),
                    RequireLinkedUser = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedExternalIds = table.Column<List<string>>(type: "text[]", nullable: false),
                    AllowUnsigned = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_messaging_channels", x => x.MessagingChannelId);
                });

            migrationBuilder.CreateTable(
                name: "messaging_deliveries",
                columns: table => new
                {
                    MessagingDeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalThreadId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_deliveries", x => x.MessagingDeliveryId);
                });

            migrationBuilder.CreateTable(
                name: "messaging_identity_links",
                columns: table => new
                {
                    MessagingIdentityLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalWorkspaceId = table.Column<string>(type: "text", nullable: true),
                    ExternalUserId = table.Column<string>(type: "text", nullable: false),
                    LinkedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_identity_links", x => x.MessagingIdentityLinkId);
                });

            migrationBuilder.CreateTable(
                name: "messaging_inbound_events",
                columns: table => new
                {
                    MessagingInboundEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderEventId = table.Column<string>(type: "text", nullable: false),
                    ExternalThreadId = table.Column<string>(type: "text", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_inbound_events", x => x.MessagingInboundEventId);
                });

            migrationBuilder.CreateTable(
                name: "messaging_link_tokens",
                columns: table => new
                {
                    MessagingLinkTokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessagingChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalWorkspaceId = table.Column<string>(type: "text", nullable: true),
                    ExternalUserId = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_link_tokens", x => x.MessagingLinkTokenId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_MessagingChannelId_ExternalThreadId",
                table: "ai_conversations",
                columns: new[] { "MessagingChannelId", "ExternalThreadId" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_CompanyId",
                table: "messaging_channels",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_CompanyId_Provider",
                table: "messaging_channels",
                columns: new[] { "CompanyId", "Provider" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_CompanyId",
                table: "messaging_deliveries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_CompanyId_At",
                table: "messaging_deliveries",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_MessagingChannelId_At",
                table: "messaging_deliveries",
                columns: new[] { "MessagingChannelId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_identity_links_CompanyId",
                table: "messaging_identity_links",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_identity_links_LinkedUserId",
                table: "messaging_identity_links",
                column: "LinkedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_identity_links_MessagingChannelId_ExternalWorkspa~",
                table: "messaging_identity_links",
                columns: new[] { "MessagingChannelId", "ExternalWorkspaceId", "ExternalUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_inbound_events_CompanyId",
                table: "messaging_inbound_events",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_inbound_events_CompanyId_At",
                table: "messaging_inbound_events",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_inbound_events_MessagingChannelId_ProviderEventId",
                table: "messaging_inbound_events",
                columns: new[] { "MessagingChannelId", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_link_tokens_CompanyId",
                table: "messaging_link_tokens",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_link_tokens_ExpiresAt",
                table: "messaging_link_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_link_tokens_TokenHash",
                table: "messaging_link_tokens",
                column: "TokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messaging_channels");

            migrationBuilder.DropTable(
                name: "messaging_deliveries");

            migrationBuilder.DropTable(
                name: "messaging_identity_links");

            migrationBuilder.DropTable(
                name: "messaging_inbound_events");

            migrationBuilder.DropTable(
                name: "messaging_link_tokens");

            migrationBuilder.DropIndex(
                name: "IX_ai_conversations_MessagingChannelId_ExternalThreadId",
                table: "ai_conversations");

            migrationBuilder.DropColumn(
                name: "ExternalThreadId",
                table: "ai_conversations");

            migrationBuilder.DropColumn(
                name: "MessagingChannelId",
                table: "ai_conversations");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ai_conversations");
        }
    }
}
