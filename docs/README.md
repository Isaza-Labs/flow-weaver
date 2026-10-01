# FlowWeaver documentation

Start with the [quick start](../QUICK_START_GUIDE.md) for a local deployment and a first draft workflow. The running app also includes a screen-by-screen manual at `/docs`; its source is in [`frontend/src/routes/docs/`](../frontend/src/routes/docs/).

## Build and operate workflows

| Topic | Guide |
|---|---|
| Deploy and configure an instance | [Deployment guide](../deploy/README.md) |
| Import inventory and workflows | [Imports](./imports.md) |
| Move workflows between instances | [Workflow bundles](./workflow-bundles.md) |
| Reuse workflow sections | [Subflows](./subflows.md) |
| Roles and capability grants | [Permissions](./permissions.md) |
| Vendor command catalogue | [Vendors](./vendors.md) |
| MCP integrations | [MCP](./mcp.md) |

## Operations and implementation

- [Python snippet sandbox](./handlers/python_snippet.md) and [email sender](./handlers/email_send.md)
- [Host requirements for Python snippets](./ops/python-sandbox-host-requirements.md)
- [Backup and disaster recovery](./ops/dr.md), [keyring rotation](./ops/keyring-rotation.md), and [load testing](./ops/load-testing.md)
- [Repository structure](./repository/README.md)
- [Backend notes](../flow_weaver_backend/README.md) and [frontend development](../frontend/README.md)

For bugs, usage questions, or private security reports, see [Support](../SUPPORT.md). This documentation describes the current development branch; check the [changelog](../CHANGELOG.md) for release status and known limitations.
