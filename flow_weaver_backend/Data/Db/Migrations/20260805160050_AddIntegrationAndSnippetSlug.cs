using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationAndSnippetSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "snippets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "integrations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_snippets_Slug",
                table: "snippets",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrations_Slug",
                table: "integrations",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_snippets_Slug",
                table: "snippets");

            migrationBuilder.DropIndex(
                name: "IX_integrations_Slug",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "snippets");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "integrations");
        }
    }
}
