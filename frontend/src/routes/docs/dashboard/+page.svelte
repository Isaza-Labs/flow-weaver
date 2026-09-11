<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Dashboard"
  lead="The landing page after sign-in. Gives you a one-glance read on fleet size, queue health, and the last runs that happened."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/"><code>/</code></a>. In the sidebar it's the first entry,
    labeled <strong>Dashboard</strong>.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      The dashboard is a read-only overview. It answers three questions at the
      top of your day:
    </p>
    <ul>
      <li>How big is the automation fleet right now?</li>
      <li>Is the execution backend healthy — how many jobs are queued or running?</li>
      <li>What has actually been running, and did it succeed?</li>
    </ul>
    <p>
      It does not let you modify anything — every card is a shortcut into the
      screen where the action lives.
    </p>
  </section>

  <section>
    <h2>Layout</h2>
    <p>
      Under the page header you'll see up to three stacked blocks, in this order:
    </p>
    <ol>
      <li><strong>Stats row</strong> — four counter tiles.</li>
      <li><strong>Job queue</strong> — one row of status pills with counts (hidden when the queue is empty).</li>
      <li><strong>Recent runs</strong> — up to eight rows, newest first.</li>
    </ol>
    <p>
      While the page is loading, a spinner replaces the whole content area. If
      any of the underlying endpoints fails, an error card with a
      <em>Retry</em> button appears instead.
    </p>
  </section>

  <section>
    <h2>Stats row</h2>
    <p>
      Four <code>StatCard</code> tiles, each a link to the matching section:
    </p>
    <dl>
      <dt>Devices</dt>
      <dd>
        Count of active devices. Clicking the tile opens
        <a href="/devices"><code>/devices</code></a>. Coloured with the primary
        accent.
      </dd>
      <dt>Workflows</dt>
      <dd>
        Count of active workflows (drafts + production). Clicking opens
        <a href="/workflows"><code>/workflows</code></a>. Coloured with the
        success accent.
      </dd>
      <dt>Total runs</dt>
      <dd>
        Total lifetime workflow runs. Clicking opens
        <a href="/runs"><code>/runs</code></a>. Neutral tone.
      </dd>
      <dt>AI skills</dt>
      <dd>
        Count of prompt skills installed for the assistant. Clicking opens
        <a href="/ai/skills"><code>/ai/skills</code></a>. Warning-tone accent.
      </dd>
    </dl>
    <Callout tone="info">
      Each counter is the <code>total</code> field returned from the matching
      list endpoint queried with <code>limit=1</code>, so a full record scan is
      never performed on the dashboard.
    </Callout>
  </section>

  <section>
    <h2>Job queue</h2>
    <p>
      A single card that lists every distinct job status currently in the engine
      queue along with its count. Statuses include <code>pending</code>,
      <code>running</code>, <code>completed</code>, <code>failed</code>,
      <code>retrying</code>, and any other state your engine emits.
    </p>
    <p>
      The card is <strong>hidden entirely</strong> when the queue is empty (no
      jobs across any status), so an idle system keeps a cleaner layout. When
      shown, each pill uses the same <code>StatusBadge</code> colour palette used
      elsewhere in the app, followed by a monospace tabular count.
    </p>
  </section>

  <section>
    <h2>Recent runs</h2>
    <p>
      A list of the eight most recent <strong>WorkflowRun</strong> rows, newest
      first. Each row is a clickable link that opens
      <code>/runs/{'{id}'}</code> (the run detail page).
    </p>

    <h3>What each row shows</h3>
    <ul>
      <li>
        <strong>Workflow name</strong> — resolved by a second fetch in parallel
        with the run list. If the workflow has been deleted, the row falls back
        to showing the truncated run id.
      </li>
      <li><strong>Run id</strong> — first 8 hex characters of the GUID, in a monospace font.</li>
      <li><strong>Trigger</strong> — the source of the run (e.g. <code>manual</code>, <code>cron</code>, the name of a scheduled trigger). Shown only when non-empty.</li>
      <li><strong>Relative time</strong> — e.g. “3 min ago”. Hidden on very narrow screens to keep rows single-line.</li>
      <li><strong>Status badge</strong> — the final or current status (<code>running</code>, <code>completed</code>, <code>failed</code>, etc).</li>
      <li><strong>Arrow</strong> — a right-pointing chevron that brightens on hover to hint at the link.</li>
    </ul>

    <p>
      The header of this section has a <em>View all →</em> link in the top-right
      corner that jumps to <a href="/runs"><code>/runs</code></a>.
    </p>

    <h3>Empty state</h3>
    <p>
      When no run has ever executed, you'll see an
      <code>EmptyState</code> card with an inbox icon and the message
      <em>“No runs yet — Workflow executions will appear here once you run one.”</em>
    </p>
  </section>

  <section>
    <h2>Behaviour and refresh</h2>
    <ul>
      <li>
        The dashboard loads once on mount. There is <strong>no auto-refresh</strong>
        on this page — if you want live activity, the better places to watch are
        <a href="/runs"><code>/runs</code></a> (list) or
        <code>/runs/{'{id}'}/monitor</code> (live run graph).
      </li>
      <li>
        If you want to see updated counts or recent runs, navigate away and back,
        or reload.
      </li>
      <li>
        All fetches run in parallel via <code>Promise.all</code>, so the page
        should feel instant on a healthy backend.
      </li>
    </ul>
  </section>

  <section>
    <h2>Role differences</h2>
    <p>
      The dashboard is visible to every authenticated user regardless of role.
      Counts reflect what the backend returns for your scope — they are
      not filtered by user. The stats, queue, and recent runs shown are the
      same whether you're an admin or a viewer.
    </p>
  </section>

  <section>
    <h2>Where to go from here</h2>
    <ul>
      <li>New to the tool? Read <a href="/docs/getting-started">Getting started</a>.</li>
      <li>Want to build something? Jump to <a href="/docs/workflows">Workflows</a>.</li>
      <li>Investigating a failure? <a href="/docs/runs">Runs</a> is the chapter you want.</li>
    </ul>
  </section>
</DocLayout>
