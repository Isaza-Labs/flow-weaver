using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddSshAuthMethodAndHostKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpectedSshHostKeyFingerprint",
                table: "devices",
                type: "text",
                nullable: true);

            // Default "password" so any pre-Phase-2 credential row stays
            // functional after the migration runs. The column is non-null
            // going forward; new key-auth credentials set it to "key"
            // explicitly at insert time.
            migrationBuilder.AddColumn<string>(
                name: "AuthMethod",
                table: "credentials",
                type: "text",
                nullable: false,
                defaultValue: "password");

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedKeyPassphrase",
                table: "credentials",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedSshHostKeyFingerprint",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "AuthMethod",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "EncryptedKeyPassphrase",
                table: "credentials");
        }
    }
}
