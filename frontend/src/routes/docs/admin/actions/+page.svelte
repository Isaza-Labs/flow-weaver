<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Reusable actions (legacy)"
  lead="A read-only view of the legacy skills database table. The page doesn't list the agent's tools, and nothing in FlowWeaver uses these rows."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/actions"><code>/admin/actions</code></a>. The page isn't
    linked from the sidebar, the admin dashboard or the command palette, so you
    have to type the address.
  </Callout>
  <Callout tone="admin" title="Admin only in the browser">
    Like every <code>/admin/…</code> route, the page sends non-admins back to the
    dashboard. The API behind it is gated differently (see
    <a href="#permissions">Permissions</a>).
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      The page heading is <strong>Reusable actions</strong>, with the subtitle
      <em>"Read-only view of the legacy skills table."</em> FlowWeaver creates no
      such rows; on most installations the list is empty.
    </p>
    <Callout tone="warning" title="Not a tool catalog">
      Neither the agent nor workflows read these rows. The agent's prompt-skill
      tools (<code>list_skills</code>, <code>load_skill</code>) read
      <em>prompt skills</em>, not this table. Nothing in the backend updates the
      <em>Use count</em> column.
    </Callout>
  </section>

  <section>
    <h2>Where to go instead</h2>
    <table>
      <thead><tr><th>You want to…</th><th>Go to</th></tr></thead>
      <tbody>
        <tr><td>Choose which tools an agent may call</td><td><a href="/ai/agents"><code>/ai/agents</code></a>, the agent's tool picker (<a href="/docs/ai/agents">AI agents</a>)</td></tr>
        <tr><td>Give the agent written guidance or procedures</td><td><a href="/ai/skills"><code>/ai/skills</code></a> (<a href="/docs/ai/skills">Prompt skills</a>)</td></tr>
        <tr><td>Let the agent call a REST API</td><td><a href="/ai/specs"><code>/ai/specs</code></a> (<a href="/docs/ai/specs">API specs</a>)</td></tr>
        <tr><td>Define a reusable call to an external system for workflows</td><td>Integration actions on <a href="/integrations"><code>/integrations</code></a> (<a href="/docs/integrations">Integrations</a>)</td></tr>
        <tr><td>Use tools from an external MCP server</td><td><a href="/admin/mcp-servers"><code>/admin/mcp-servers</code></a> (<a href="/docs/admin/mcp-servers">MCP servers</a>)</td></tr>
        <tr><td>Build a reusable workflow step</td><td><a href="/snippets"><code>/snippets</code></a> (<a href="/docs/snippets">Snippets</a>)</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Concepts and limits</h2>
    <ul>
      <li>
        <strong>Where rows come from.</strong> FlowWeaver doesn't create any rows
        itself: no seed data and no UI. A row exists only if it was created through
        <code>POST /api/skill</code> or carried over from an older database. On
        most installations the table is empty.
      </li>
      <li>
        <strong>The page is read-only.</strong> The API still has create, update
        and delete endpoints (<code>/api/skill</code>), but nothing in the UI calls
        them. Delete is a soft delete.
      </li>
      <li>
        <strong>Only the first 50 rows</strong> are requested. The page has no
        paging or search, and the row counter under the table counts only the rows
        loaded.
      </li>
    </ul>
  </section>

  <section>
    <h2>The screen and its fields</h2>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Name</td><td>The row name, with its description on a second line, cut to one line.</td></tr>
        <tr><td>Type</td><td>The stored <code>skill_type</code> string, or <code>—</code>.</td></tr>
        <tr><td>Triggers</td><td>The stored trigger phrases, as badges.</td></tr>
        <tr><td>Tags</td><td>The stored tags, as badges.</td></tr>
        <tr><td>Use count</td><td>The stored <code>use_count</code> (<code>0</code> when absent). No part of the system increments it.</td></tr>
      </tbody>
    </table>
    <p>
      Below the table, a counter reads <em>N action(s)</em>. The API returns more
      fields (action config, parameters, examples, enabled flag, and others), but
      the page doesn't display them.
    </p>
  </section>

  <section id="permissions">
    <h2>Permissions and security</h2>
    <ul>
      <li>The page is for admins only (browser route guard).</li>
      <li>
        The API is governed by capabilities rather than the admin role:
        <code>skill.read</code> (viewer tier by default) for reads and
        <code>skill.manage</code> (operator tier) for writes. The same
        <code>skill.read</code> capability also gates the agent's prompt-skill
        tools.
      </li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <dl>
      <dt>Loading</dt><dd>A large spinner.</dd>
      <dt>No rows</dt><dd><em>"No reusable actions"</em>, with the hint <em>"Entries will appear here once defined."</em> This is the normal state.</dd>
      <dt>Load error</dt>
      <dd>
        A red alert with the message and an <em>"Action failed"</em> toast. The
        empty state appears under the alert too, so check the alert before you
        conclude the table is empty. Reload the page to try again, since there's no
        refresh button.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>You can't view, create, edit or delete rows from the UI.</li>
      <li>The page isn't linked from any navigation.</li>
      <li>The rows have no effect on the agent, workflows or runs.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai/agents">AI agents</a></li>
      <li><a href="/docs/ai/skills">Prompt skills</a></li>
      <li><a href="/docs/ai/specs">API specs</a></li>
      <li><a href="/docs/integrations">Integrations</a></li>
      <li><a href="/docs/admin/mcp-servers">MCP servers</a></li>
    </ul>
  </section>
</DocLayout>
