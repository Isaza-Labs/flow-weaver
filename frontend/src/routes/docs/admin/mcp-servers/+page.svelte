<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="MCP servers"
  lead="Register external Model Context Protocol servers so the AI agent and your workflows can call their tools — with encrypted credentials, OAuth 2.1, and per-server/per-tool permissions."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Integrate</strong> → <strong>MCP servers</strong>
    (<a href="/admin/mcp-servers"><code>/admin/mcp-servers</code></a>).
  </Callout>
  <Callout tone="admin" title="Admin only">
    Registering and editing servers requires the <code>admin</code> role (they hold
    connection secrets). Any user with the right permissions can then <em>use</em>
    the tools from chat or a workflow.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      <strong>MCP</strong> (Model Context Protocol) is an open standard for exposing
      <em>tools</em> to AI agents. FlowWeaver acts as an MCP <strong>client</strong>:
      you register a remote server, and its tools become available to the
      <strong>chat agent</strong> and to a new <strong>MCP call</strong> workflow
      node — both through the same executor.
    </p>
    <p>
      v1 supports remote <strong>Streamable HTTP</strong> servers and their
      <strong>tools</strong> (not resources/prompts, and no local subprocesses).
    </p>
  </section>

  <section>
    <h2>Registering a server</h2>
    <p>
      Click <strong>Add server</strong>, give it a name and URL, pick an
      authentication method, then click <strong>Test &amp; sync tools</strong> to
      connect and cache its tools. Status shows as <code>ok</code>,
      <code>needs_config</code>, <code>needs_authorization</code>, or
      <code>unreachable</code>.
    </p>
    <h3>Authentication</h3>
    <p>Every secret is encrypted at rest and never shown again (you re-enter it only to change it).</p>
    <ul>
      <li><strong>None</strong> — no auth.</li>
      <li><strong>API key</strong> — a header name (default <code>X-API-Key</code>) + value.</li>
      <li><strong>Bearer token</strong> — an <code>Authorization: Bearer</code> token.</li>
      <li><strong>Basic</strong> — a username + password, sent as <code>Authorization: Basic</code> (base64 of <code>user:password</code>). The username is stored in the clear; the password is encrypted.</li>
      <li><strong>Custom secret headers</strong> — a JSON object of header → value.</li>
      <li><strong>OAuth — client credentials</strong> — machine-to-machine; paste client id + secret and FlowWeaver fetches/refreshes the token automatically.</li>
      <li><strong>OAuth — authorization code</strong> — save the server, then click <strong>Authorize</strong> to consent via redirect (PKCE, with automatic discovery and token refresh).</li>
    </ul>
  </section>

  <section>
    <h2>Using the tools</h2>
    <p>
      <strong>In chat:</strong> the agent lists servers, discovers tools, and calls
      them (calling a tool asks for confirmation, like other mutating actions).
    </p>
    <p>
      <strong>In a workflow:</strong> open the builder's left palette, expand the
      <strong>MCP</strong> section, and drag a tool onto the canvas. Pick the server
      and tool, then fill in the arguments — you can reference upstream outputs with
      <code>&#123;&#123; steps.&lt;node&gt;.output.&lt;field&gt; &#125;&#125;</code>.
    </p>
  </section>

  <section>
    <h2>Permissions</h2>
    <p>Create a permission grant (Govern → Permissions, see <a href="/docs/permissions">Permissions</a>) with:</p>
    <ul>
      <li><code>mcpserver.read</code> / <code>mcpserver.manage</code> — view / manage servers.</li>
      <li><code>mcp.read</code> — let the agent discover tools.</li>
      <li><code>mcp.execute</code> — call a tool. This can be <strong>scoped by server and by tool</strong>, so a user (or a Slack/Telegram channel) can be limited to exactly the tools they should run. The visual condition builder has no fields for these two scopes — write them in the grant's <em>JSON</em> tab. Conditions are only enforced in <code>granular</code> RBAC mode.</li>
    </ul>
  </section>

  <Callout tone="warning" title="Trust the servers you add">
    A tool's description comes from the external server and is shown to the AI. Only
    register servers you trust, and treat tool descriptions and outputs as data — the
    agent is instructed never to follow instructions embedded in them.
  </Callout>
</DocLayout>
