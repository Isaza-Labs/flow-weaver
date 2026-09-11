using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_channels",
                columns: table => new
                {
                    EmailChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Security = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    EncryptedPassword = table.Column<byte[]>(type: "bytea", nullable: true),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: true),
                    ReplyTo = table.Column<string>(type: "text", nullable: true),
                    AllowPrivateNetwork = table.Column<bool>(type: "boolean", nullable: false),
                    TlsSkipVerify = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSendAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSendStatus = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_channels", x => x.EmailChannelId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_channels_IsDefault_Enabled",
                table: "email_channels",
                columns: new[] { "IsDefault", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_email_channels_Provider",
                table: "email_channels",
                column: "Provider");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_channels");
        }
    }
}
