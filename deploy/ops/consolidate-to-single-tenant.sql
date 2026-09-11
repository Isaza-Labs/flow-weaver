-- Consolidate a multi-company database down to one, so the RemoveMultiTenancy
-- migration can run (see company_remove.md).
--
-- DESTRUCTIVE: every row belonging to a non-canonical company is DELETED.
-- Intended for development / pre-production databases. Take a dump first.
--
-- Why deleting is enough: the old unique indexes were scoped by CompanyId, so
-- values were already unique *within* each company. Once a single company's
-- rows remain, every index that loses CompanyId is unique by construction —
-- no per-table deduplication is needed.
--
-- Run BEFORE deploying the build that carries RemoveMultiTenancy:
--   docker compose -f deploy/docker-compose.yml exec -T db \
--     psql -U flowweaver -d flowweaver < deploy/ops/consolidate-to-single-tenant.sql
--
-- The canonical company is the oldest active one — the same row the
-- AddAppSettings migration copies its settings from, so the two agree.

BEGIN;

DO $$
DECLARE
    canonical uuid;
    kept_name text;
    t         record;
    removed   bigint;
    total     bigint := 0;
BEGIN
    IF to_regclass('public.companies') IS NULL THEN
        RAISE NOTICE 'No companies table — already migrated, nothing to do.';
        RETURN;
    END IF;

    SELECT "CompanyId", "Name" INTO canonical, kept_name
    FROM companies
    ORDER BY "IsActive" DESC, "CreatedAt" ASC
    LIMIT 1;

    IF canonical IS NULL THEN
        RAISE NOTICE 'companies table is empty — nothing to consolidate.';
        RETURN;
    END IF;

    RAISE NOTICE 'Canonical company: % (%)', kept_name, canonical;

    -- Every base table that still carries the discriminator. Discovered from
    -- the catalog so the list cannot drift from the schema.
    FOR t IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
        WHERE c.table_schema = 'public'
          AND c.column_name  = 'CompanyId'
          AND tb.table_type  = 'BASE TABLE'
          AND c.table_name  <> 'companies'
        ORDER BY c.table_name
    LOOP
        -- NULL is kept: auth_events uses it for login attempts that never
        -- resolved to a user, and NULL never collides in a unique index.
        EXECUTE format(
            'DELETE FROM %I WHERE "CompanyId" IS NOT NULL AND "CompanyId" <> $1',
            t.table_name)
        USING canonical;

        GET DIAGNOSTICS removed = ROW_COUNT;
        IF removed > 0 THEN
            RAISE NOTICE '  % -> % row(s) deleted', t.table_name, removed;
            total := total + removed;
        END IF;
    END LOOP;

    DELETE FROM companies WHERE "CompanyId" <> canonical;
    GET DIAGNOSTICS removed = ROW_COUNT;

    RAISE NOTICE 'companies -> % row(s) deleted', removed;
    RAISE NOTICE 'Total foreign-company rows deleted: %', total;
END $$;

COMMIT;

-- Verification: both must come back empty / 1.
\echo ''
\echo '--- companies restantes (expect 1) ---'
SELECT count(*) AS companies FROM companies;

\echo ''
\echo '--- duplicados que aun bloquearian la migracion (expect 0 rows) ---'
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
