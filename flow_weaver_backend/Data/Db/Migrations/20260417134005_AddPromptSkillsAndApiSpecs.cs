using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptSkillsAndApiSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_api_specs",
                columns: table => new
                {
                    AiApiSpecId = table.Column<Guid>(type: "uuid", nullable: false),
                    Api = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    OperationCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_api_specs", x => x.AiApiSpecId);
                });

            migrationBuilder.CreateTable(
                name: "ai_prompt_skills",
                columns: table => new
                {
                    AiPromptSkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_prompt_skills", x => x.AiPromptSkillId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId",
                table: "ai_api_specs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId_Api",
                table: "ai_api_specs",
                columns: new[] { "CompanyId", "Api" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId_IsActive",
                table: "ai_api_specs",
                columns: new[] { "CompanyId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId",
                table: "ai_prompt_skills",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId_IsActive_SortOrder_Name",
                table: "ai_prompt_skills",
                columns: new[] { "CompanyId", "IsActive", "SortOrder", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId_Name",
                table: "ai_prompt_skills",
                columns: new[] { "CompanyId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_api_specs");

            migrationBuilder.DropTable(
                name: "ai_prompt_skills");
        }
    }
}
