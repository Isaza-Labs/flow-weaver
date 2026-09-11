<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Traces"
  lead="Application-level action trail. Every critical handler (AI chat, tool dispatch, auth login, workflow enqueue, ...) writes a row here via ITraceLogger. Geared for live debugging."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/traces"><code>/admin/traces</code></a>.
  </Callout>
  <Callout tone="admin" title="Admin only">
    Requires the <code>admin</code> role.
  </Callout>

  <section>
    <h2>Audit log vs traces</h2>
    <p>
      The two admin surfaces look similar but serve different purposes:
    </p>
    <dl>
      <dt><a href="/docs/admin/audit">Audit log</a></dt>
      <dd>
        One row per <em>domain write</em>. Designed for compliance and
        "who changed what" retrospectives. Retention is long.
      </dd>
      <dt>Traces (this page)</dt>
      <dd>
        One row per <em>handler invocation</em>, including reads and
        tool dispatches. Designed for live debugging: "why did this chat
        call take 18 seconds?", "which tool failed and with what exception?".
        Retention is shorter.
      </dd>
    </dl>
    <Callout tone="info">
      Every trace row carries a <strong>request_id</strong> that also appears
      in the Serilog structured logs. Copy the id from a row to pivot to
      <code>docker logs</code> for the full backend stack trace.
    </Callout>
  </section>

  <section>
    <h2>Filters</h2>
    <p>
      Stacked above the list:
    </p>
    <dl>
      <dt>Category</dt><dd>Coarse grouping — <code>ai</code>, <code>auth</code>, <code>workflow</code>, <code>integration</code>, etc.</dd>
      <dt>Action</dt><dd>Specific handler name — <code>chat.stream</code>, <code>tool.dispatch</code>, <code>login.succeeded</code>, …</dd>
      <dt>Status</dt><dd>Usually <code>ok</code> / <code>failed</code> / <code>timeout</code>.</dd>
      <dt>User</dt><dd>Acting user. Select populated from <code>/admin/users</code>.</dd>
      <dt>Request id</dt><dd>Exact-match filter. Pivots from a single Serilog line back to the trace row.</dd>
      <dt>From / to</dt><dd>Date inputs.</dd>
    </dl>
  </section>

  <section>
    <h2>Live mode</h2>
    <p>
      The <em>Live</em> toggle in the header starts a 5-second polling
      interval. Off by default so stable filters don't keep jumping. Useful
      when you're watching a specific user's session in real time.
    </p>
    <p>
      When live, the pause/play icon flips between <em>Pause</em> and
      <em>Live</em> states so you always know which mode you're in.
    </p>
  </section>

  <section>
    <h2>Row</h2>
    <p>
      Each trace row carries:
    </p>
    <ul>
      <li>Timestamp.</li>
      <li>Category, coloured by kind.</li>
      <li>Action string.</li>
      <li>Status badge (ok/failed/timeout).</li>
      <li>Duration in milliseconds.</li>
      <li>Error message when status is failed.</li>
      <li>Actor username (or raw UUID when no lookup).</li>
      <li>Request id (clickable to copy).</li>
    </ul>

    <h3>Expander</h3>
    <p>
      Clicking the chevron opens a JSON metadata panel. What's inside depends
      on the category — for AI chat turns it includes the conversation id,
      token counts, and the tool chain; for tool dispatches it includes
      arguments preview and preview of the result. For auth it includes IP
      and the outcome reason.
    </p>
    <p>
      The copy-to-clipboard button on the request id puts
      <code>request_id=abc-123</code> on your clipboard in the exact shape
      Serilog uses, so you can paste-search a log file directly.
    </p>
  </section>

  <section>
    <h2>Empty states</h2>
    <ul>
      <li>No traces in the filter window → <em>Activity</em>-icon empty state.</li>
      <li>Error loading → an Alert at the top with the server message.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/admin">Admin dashboard</a> — summarises the same stream as charts.</li>
      <li><a href="/docs/admin/audit">Audit log</a> — the compliance-oriented sibling.</li>
    </ul>
  </section>
</DocLayout>
