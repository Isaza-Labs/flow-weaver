# API Specs

Place one OpenAPI 3.x YAML file per external system in this folder. The
filename (without `.yaml`) becomes the `api` prefix used by the agent's
`discover` / `detail` / `execute` tools.

Examples:
- `netbox.yaml`      → operations available under `netbox:*`
- `servicenow.yaml`  → operations available under `servicenow:*`

At startup every spec is synced into the database (a shipped spec an
admin edited in the UI keeps the admin's version) and indexed in memory.
After editing a spec on disk, use **Reseed from disk** on `/ai/specs`
(`POST /api/admin/api-specs/reseed`, admin; it overwrites admin edits of
shipped specs). `POST /api/ai/catalog/reload` only re-indexes what is
already in the database.

Minimal valid spec:

```yaml
openapi: "3.0.3"
info:
  title: "Example API"
  version: "1.0.0"
paths:
  /widgets:
    get:
      operationId: list_widgets
      summary: "List all widgets"
      tags: [widgets]
```
