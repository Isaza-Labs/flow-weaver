using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorCommandSemantics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "vendor_commands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Intent",
                table: "vendor_commands",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "vendor_commands");

            migrationBuilder.DropColumn(
                name: "Intent",
                table: "vendor_commands");
        }
    }
}
