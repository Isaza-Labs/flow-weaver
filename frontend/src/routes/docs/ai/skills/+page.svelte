<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="AI prompt skills"
  lead="Markdown fragments concatenated into every chat's system prompt. Sortable, filterable by integration scope, with placeholder interpolation for current date and tool list."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/ai/skills"><code>/ai/skills</code></a>.
    Sidebar → <strong>Intelligence</strong> → <strong>AI</strong> → Agent
    composition card → <em>Prompt skills</em>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A <strong>prompt skill</strong> is a stored Markdown file
      that the chat runner prepends to every conversation's system prompt.
      Skills let you teach the assistant local facts, tone rules,
      tool-usage hints, or domain vocabulary without editing the agent's
      system prompt every time.
    </p>
    <p>
      Every chat assembles its system prompt in this order:
    </p>
    <ol>
      <li>All <strong>active</strong> skills concatenated by <code>sort_order</code> (ascending), then by <code>name</code>.</li>
      <li>Placeholder interpolation — see below.</li>
      <li>The agent's optional <code>system_prompt</code> string.</li>
    </ol>
    <p>
      The <code>SkillPromptLoader</code> cache invalidates on every save so the
      next message picks up edits immediately.
    </p>
  </section>

  <section>
    <h2>Placeholders</h2>
    <p>
      These Jinja-style placeholders are replaced at assembly time:
    </p>
    <dl>
      <dt><code>{'{{CurrentDate}}'}</code></dt>
      <dd>ISO date in the default timezone — useful for "as of today" prompts.</dd>
      <dt><code>{'{{ToolList}}'}</code></dt>
      <dd>Formatted list of every tool the agent is allowed to call. Keeps your skill honest about what the assistant can actually do.</dd>
    </dl>
  </section>

  <section>
    <h2>Integration scoping</h2>
    <p>
      A skill can be <strong>global</strong> (loaded in every conversation)
      or <strong>integration-scoped</strong> (loaded only when a conversation
      is known to be about that integration). Scoping happens via the
      <code>integration_id</code> foreign key on the skill row.
    </p>
    <p>
      Integration-scoped skills are created automatically when you upload
      them through the <a href="/docs/integrations">New integration</a>
      bundle wizard. You can also reassign them later from this page's edit
      dialog.
    </p>
  </section>

  <section>
    <h2>List page</h2>
    <p>
      Route: <code>/ai/skills</code>. Header with <em>Refresh</em>,
      <em>Upload</em>, and <em>New skill</em>. The <em>Upload</em> button
      fires a hidden file input that stages one or more <code>.md</code>
      files and opens them in the edit dialog for review before saving.
    </p>

    <h3>Integration filter</h3>
    <p>
      A select above the table: <em>All</em> (default), <em>Global only</em>,
      or a specific integration by name. Integration names come from
      <a href="/integrations"><code>/integrations</code></a> and are loaded
      once on mount. Skills whose <code>integration_id</code> points at a
      deleted integration show the raw UUID as a hint.
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Name</td><td>Skill identifier, monospace.</td></tr>
        <tr><td>Integration</td><td>Integration name, or <em>Global</em> when unscoped.</td></tr>
        <tr><td>Order</td><td>Integer. Lower values are concatenated earlier.</td></tr>
        <tr><td>Active</td><td>StatusBadge — inactive skills stay in the catalogue but don't ship in prompts.</td></tr>
        <tr><td>Updated</td><td>Last save time.</td></tr>
        <tr><td>Actions</td><td>Edit, download, delete.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Create / edit dialog</h2>
    <p>
      Same dialog handles new and existing. Fields:
    </p>
    <dl>
      <dt>Name</dt>
      <dd>
        Must match <code>^[a-zA-Z0-9_\-]+\.md$</code> — alphanumerics,
        underscore, hyphen, trailing <code>.md</code>. Used as the skill id.
      </dd>
      <dt>Sort order</dt>
      <dd>Integer. Default 100. Lower wins the composition order.</dd>
      <dt>Content</dt>
      <dd>Large Markdown textarea. Supports the two placeholders above.</dd>
      <dt>Active</dt><dd>Checkbox.</dd>
      <dt>Integration</dt>
      <dd>
        Select from the configured integrations, or <em>Global</em>.
        Re-assigning changes the scope without touching content.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Delete</h2>
    <p>
      Confirmation dialog. Deletes the skill. Conversations
      that referenced content from this skill keep their historical messages
      but future turns won't include it.
    </p>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — list and download.</li>
      <li><strong>Operator</strong> — full CRUD, upload, active toggle.</li>
      <li><strong>Admin</strong> — same as operator.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai/specs">API specs</a> — the other input to the agent context.</li>
      <li><a href="/docs/integrations">Integrations</a> — source of scoped skills uploaded at bundle time.</li>
    </ul>
  </section>
</DocLayout>
