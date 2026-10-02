-- Pre-deploy check for the multi-tenancy removal (see deploy/README.md).
--
-- Eleven unique indexes used to be scoped by CompanyId. The RemoveMultiTenancy
-- migration collapses each to its remaining columns, so any value that was only
-- unique *within* a company now collides. The migration refuses to run in that
-- case — this script tells you BEFORE the deploy instead of after the backend
-- starts crash-looping on boot.
--
--   docker compose -f deploy/docker-compose.yml exec -T db \
--     psql -U flowweaver -d flowweaver < deploy/ops/precheck-single-tenant.sql
--
-- Expected output: "companies_activas | 1" (or 0) and NO duplicate rows.
-- Anything listed under "duplicados" must be deduplicated (rename or delete)
-- before deploying.

\echo '--- active companies (expect 0 or 1) ---'
SELECT count(*) AS companies_activas FROM companies WHERE "IsActive";

\echo ''
\echo '--- duplicados que bloquean la migracion (expect: 0 rows) ---'
SELECT 'users("Username")' AS tabla, "Username" AS valor, count(*) AS filas
    FROM users GROUP BY "Username" HAVING count(*) > 1
UNION ALL
SELECT 'secrets("Name")', "Name", count(*)
    FROM secrets GROUP BY "Name" HAVING count(*) > 1
UNION ALL
SELECT 'allowed_python_modules("ImportName")', "ImportName", count(*)
    FROM allowed_python_modules GROUP BY "ImportName" HAVING count(*) > 1
UNION ALL
SELECT 'ai_prompt_skills("Name")', "Name", count(*)
    FROM ai_prompt_skills GROUP BY "Name" HAVING count(*) > 1
UNION ALL
SELECT 'ai_api_specs("Api")', "Api", count(*)
    FROM ai_api_specs GROUP BY "Api" HAVING count(*) > 1
UNION ALL
SELECT 'git_repositories("Name")', "Name", count(*)
    FROM git_repositories WHERE "IsActive" GROUP BY "Name" HAVING count(*) > 1
UNION ALL
SELECT 'agent_scratches("ConversationId","Key")', "ConversationId" || ' / ' || "Key", count(*)
    FROM agent_scratches WHERE "IsActive"
    GROUP BY "ConversationId", "Key" HAVING count(*) > 1
UNION ALL
SELECT 'mcp_tools("McpServerId","Name")', "McpServerId" || ' / ' || "Name", count(*)
    FROM mcp_tools WHERE "IsActive"
    GROUP BY "McpServerId", "Name" HAVING count(*) > 1
ORDER BY 1, 2;

\echo ''
\echo '--- per-company pip package dirs (informativo, para migrate-pyenv-to-site.sh) ---'
SELECT count(DISTINCT "CompanyId") AS companies_con_paquetes
    FROM allowed_python_modules;
