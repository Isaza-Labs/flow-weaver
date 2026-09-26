using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class CatalogShippedContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShippedContentHash",
                table: "ai_prompt_skills",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippedContentHash",
                table: "ai_api_specs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShippedContentHash",
                table: "ai_prompt_skills");

            migrationBuilder.DropColumn(
                name: "ShippedContentHash",
                table: "ai_api_specs");
        }
    }
}
