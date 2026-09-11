# API Specs

Place one OpenAPI 3.x YAML file per external system in this folder. The
filename (without `.yaml`) becomes the `api` prefix used by the agent's
`discover` / `detail` / `execute` tools.

Examples:
- `netbox.yaml`      → operations available under `netbox:*`
- `servicenow.yaml`  → operations available under `servicenow:*`

Each spec is parsed at startup into an in-memory index. After editing a
spec on disk, hit `POST /api/ai/catalog/reload` to re-index without
restarting the server.

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
