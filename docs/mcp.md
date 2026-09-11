# MCP servers

Flow-weaver can act as a **Model Context Protocol (MCP) client**: register an
external MCP server and its tools become available to the **chat agent** and to
**workflows** (the `mcp_call` node) — both through one shared executor.

Scope (v1): remote **Streamable HTTP** servers only (no local `stdio` subprocesses),
and MCP **tools** only (not resources/prompts).

---

## Registering a server

**Govern → MCP servers** (admin only) → **Add server**:

| Field | Notes |
|---|---|
| **Name** | Display name used in the palette and agent. |
| **Server URL** | The server's HTTP endpoint (e.g. `https://mcp.example.com/mcp`). Passed through the SSRF guard. |
| **Authentication** | See below. |
| **Skip TLS verification** | Lab/dev only. |
| **Allow private / loopback network** | SSRF opt-out for THIS server (e.g. an on-prem MCP server on `10.0.0.0/8`). Never unblocks loopback or `169.254.169.254`. |

After creating a server, click **Test & sync tools** — flow-weaver connects,
runs `tools/list`, and caches the tools (`status → ok`). Tools that later
disappear from the server are removed on the next sync.

### Authentication

Every credential is **encrypted at rest** (`ICredentialEncryptionService`) and is
never returned by the API — the UI shows an "already set" hint and you re-enter a
secret only to change it.

- **None** — no auth header.
- **API key (header)** — a header name (default `X-API-Key`) + value.
- **Bearer token** — `Authorization: Bearer <token>`.
- **Custom secret headers** — a JSON object of header → value.
- **OAuth — client credentials** — machine-to-machine. Paste `client_id` +
  `client_secret` (and, optionally, the token endpoint / scopes, or let discovery
  find them). Flow-weaver fetches and refreshes the token automatically; no user
  interaction.
- **OAuth — authorization code** — admin consent via redirect. Save the server,
  then click **Authorize** on its row. Flow-weaver discovers the authorization
  server (`.well-known`), registers a client dynamically (RFC 7591) when the AS
  supports it (otherwise paste `client_id`), and runs the authorization-code + PKCE
  flow. Tokens are stored encrypted and refreshed automatically; if a refresh
  fails the status flips to `needs_authorization` and you re-run **Authorize**.

### The OAuth redirect URI

The callback URL is derived from the browser-facing origin of the **Authorize**
request: behind the shipped SvelteKit proxy that origin travels in
`X-Forwarded-Host` / `X-Forwarded-Proto`, which the backend honours from proxies
listed in `Network__TrustedProxies` (docker-compose trusts the compose network by
default). With the default ports it is:

```
http://localhost:3000/api/mcp-servers/<server-guid>/oauth/callback
```

Register exactly this URL with providers that require pre-registered redirect URIs.
If `Network__TrustedProxies` is unset, the forwarded headers are ignored and the
redirect URI falls back to the backend's own host — unreachable from a browser in
the shipped topology, so keep the trust list set.

Provider notes (Google, as the common case): create a **Web application** OAuth
client (Desktop clients have no redirect-URI list); `scope` is mandatory when the
endpoints are typed manually (no discovery to supply one); append
`?access_type=offline&prompt=consent` to the authorization endpoint or Google
issues no refresh token and the grant dies with the first access token; and
Google's *hosted* Gmail MCP server additionally requires the Cloud project to be
enrolled in the Workspace Developer Preview Program — without it `tools/list`
works but every call fails.

Status values: `ok`, `needs_config`, `needs_authorization`, `unreachable`.

---

## Using MCP tools in chat

The agent has three tools (grant `mcp.read` to discover, `mcp.execute` to call):

1. `list_mcp_servers` — enabled servers (id, name, status, tool count).
2. `discover_mcp_tools` — search cached tools by keyword / server; returns each
   tool's input schema.
3. `call_mcp_tool` — invoke a tool (`single_confirm` tier; counts against the
   mutation budget).

The discovery-before-call discipline and safety notes live in `Skills/mcp.md`.

## Using MCP tools in workflows

In the workflow builder, open the left palette's **MCP** section, expand a server,
and drag a tool onto the canvas. The node stores `mcp_server_id`, `tool_name`, and
an `arguments` object in its `config_overrides`; `arguments` accepts templates like
`{{ steps.<node-id>.output.<field> }}`. The node is `requires_compensation` (its
external effect is unknown) and its output is `{ content, structured, is_error }`.

---

## Permissions (RBAC)

| Capability | Tier | Meaning |
|---|---|---|
| `mcpserver.read` | Viewer | View servers + their tools. |
| `mcpserver.manage` | Admin | Create/edit/delete servers (holds secrets). |
| `mcp.read` | Viewer | Discover tools (agent). |
| `mcp.execute` | Operator | Call a tool. **Conditionable by server and by tool.** |

`mcp.execute` supports two ABAC condition keys in a permission grant:
`mcp_server` (a list of server UUIDs) and `mcp_tool` (a list of tool names, matched
case-sensitively). A grant with both restricts the user to that tool on that
server. Enforcement is identical from the web, the agent, and messaging channels
(a channel can only narrow a user's capabilities, never widen them). In granular
mode the same `mcp.execute` check runs when a **workflow is saved** — you can't
author an `mcp_call` node for a server/tool you aren't allowed to call.

---

## Security posture

- **SSRF** — every server URL and every discovered OAuth endpoint passes
  `IUrlGuard.EnsureSafe`; loopback / link-local / `169.254.169.254` stay blocked
  even with *Allow private network*. The guard covers IPv4, IPv6 (link-local
  `fe80::/10`, unique-local `fc00::/7`, `::1`, `::`), and IPv4-mapped IPv6
  (`::ffff:169.254.169.254` can't smuggle the metadata IP past the IPv4 rules).
- **Secrets** — auth material and OAuth tokens are encrypted at rest and omitted
  from every API response.
- **Timeouts + caps** — each connection/operation runs under a linked-CTS budget;
  tool output is size-capped before it reaches the LLM or a step output.
- **Tool poisoning / prompt injection** — tool *descriptions come from the external
  server* and are shown to the model. Only admins can register servers, tools are
  namespaced as MCP in the prompt, and `Skills/mcp.md` instructs the agent to treat
  tool descriptions/outputs as data, never as instructions. Re-sync is explicit.

## Deferred (non-blocking) follow-ups

- Enqueue-time readiness gate for `mcp_call` nodes whose server is not `ok` (today
  the node fails gracefully at run time instead).
- Connection/session pooling (v1 is connect-per-operation).
- MCP resources & prompts, and the local `stdio` transport.
