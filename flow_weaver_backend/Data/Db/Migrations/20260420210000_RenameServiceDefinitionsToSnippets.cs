using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class RenameServiceDefinitionsToSnippets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rewrite workflow.Nodes JSON: `"service_def_id"` → `"snippet_id"`.
            // Idempotent — if already migrated the REPLACE matches nothing.
            migrationBuilder.Sql(@"
                UPDATE workflows
                SET ""Nodes"" = REPLACE(""Nodes""::text, '""service_def_id""', '""snippet_id""')::jsonb
                WHERE ""Nodes""::text LIKE '%""service_def_id""%';
            ");

            // Rename the FK column on step_runs.
            migrationBuilder.RenameColumn(
                name: "ServiceDefId",
                table: "step_runs",
                newName: "SnippetId");

            // Rename the service_definitions table + its PK and indices.
            migrationBuilder.RenameTable(
                name: "service_definitions",
                newName: "snippets");

            migrationBuilder.RenameColumn(
                name: "ServiceDefinitionId",
                table: "snippets",
                newName: "SnippetId");

            migrationBuilder.RenameIndex(
                name: "IX_service_definitions_Type",
                table: "snippets",
                newName: "IX_snippets_Type");

            migrationBuilder.RenameIndex(
                name: "IX_service_definitions_CompanyId",
                table: "snippets",
                newName: "IX_snippets_CompanyId");

            migrationBuilder.Sql(@"
                ALTER TABLE snippets RENAME CONSTRAINT ""PK_service_definitions"" TO ""PK_snippets"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE snippets RENAME CONSTRAINT ""PK_snippets"" TO ""PK_service_definitions"";
            ");

            migrationBuilder.RenameIndex(
                name: "IX_snippets_CompanyId",
                table: "snippets",
                newName: "IX_service_definitions_CompanyId");

            migrationBuilder.RenameIndex(
                name: "IX_snippets_Type",
                table: "snippets",
                newName: "IX_service_definitions_Type");

            migrationBuilder.RenameColumn(
                name: "SnippetId",
                table: "snippets",
                newName: "ServiceDefinitionId");

            migrationBuilder.RenameTable(
                name: "snippets",
                newName: "service_definitions");

            migrationBuilder.RenameColumn(
                name: "SnippetId",
                table: "step_runs",
                newName: "ServiceDefId");

            migrationBuilder.Sql(@"
                UPDATE workflows
                SET ""Nodes"" = REPLACE(""Nodes""::text, '""snippet_id""', '""service_def_id""')::jsonb
                WHERE ""Nodes""::text LIKE '%""snippet_id""%';
            ");
        }
    }
}
