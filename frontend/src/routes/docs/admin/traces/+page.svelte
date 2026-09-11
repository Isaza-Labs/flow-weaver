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
      <dt>Search</dt>
      <dd>
        One term matched <em>anywhere</em> in the action, the category, the error message,
        the request id or the metadata JSON — <code>chat</code> finds
        <code>ai.chat.stream</code>, <code>timeout</code> finds the rows that failed that
        way, and pasting a request id here reconstructs that call. Substring, not a
        prefix, and <code>%</code> or <code>_</code> in the term are characters you typed,
        not wildcards.
      </dd>
      <dt>User</dt>
      <dd>
        Who set the action off, by user id, username or email — whichever you happen to be
        holding. Blank on rows with no signed-in user behind them: the seeder, the
        retention sweeper, anything the worker started on its own.
      </dd>
      <dt>Category</dt><dd>Coarse grouping — <code>ai</code>, <code>auth</code>, <code>workflow</code>, <code>integration</code>, etc.</dd>
      <dt>Status</dt><dd>Usually <code>ok</code> / <code>failed</code> / <code>timeout</code>.</dd>
      <dt>Slower than (ms)</dt>
      <dd>
        Only rows that finished and took at least this long — the query that finds the
        problem before anything has actually failed. It is also the one filter that
        changes the <em>order</em>: results come back slowest first, because the newest
        rows of a wide window are not the slow ones you asked for. Rows still marked
        <code>started</code> have no duration yet and never match.
      </dd>
      <dt>Window</dt>
      <dd>
        Bounds every query — last 15 minutes through 7 days, all time, or
        <em>Custom range</em>, which hands over to the two date pickers. Defaults to all
        time; the table grows on every request, so narrow this before widening anything
        else, and especially before <em>Slower than</em>, which otherwise ranks the
        slowest rows of the whole history.
      </dd>
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
