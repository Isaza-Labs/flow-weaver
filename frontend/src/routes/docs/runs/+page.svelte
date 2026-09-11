<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Runs"
  lead="The execution history of every workflow. Each run carries its status, the full step timeline, input/output payloads, live streaming while it's in flight, and an admin-only prune to clear history."
>
  <Callout tone="where" title="Where to find it">
    List: <a href="/runs"><code>/runs</code></a> ·
    Detail: <code>/runs/{'{id}'}</code> ·
    Monitor: <code>/runs/{'{id}'}/monitor</code>.
    Sidebar → <strong>Operate</strong> → <strong>Runs</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A <strong>run</strong> is one invocation of a workflow. The engine creates
      a <code>workflow_run</code> row at enqueue time, freezes a snapshot of the
      DAG into <code>nodes_snapshot</code> and <code>edges_snapshot</code>, then
      produces one <strong>step run</strong> per node execution. Runs are
      read-only from the UI; only admins can prune them after the fact.
    </p>
    <p>
      Every run has three views in the app:
    </p>
    <dl>
      <dt>List</dt>
      <dd>One row per run. History with status, trigger, and duration.</dd>
      <dt>Detail</dt>
      <dd>Step-by-step payload inspector plus a transform playground. Good for post-mortem and data-flow debugging.</dd>
      <dt>Monitor</dt>
      <dd>The live DAG graph with coloured nodes and a right-hand step timeline. Streams via WebSocket while the run is in flight.</dd>
    </dl>
  </section>

  <section>
    <h2>The list page</h2>
    <p>
      Route: <code>/runs</code>. Header with a <em>Refresh</em> button and,
      for admins, a red <em>Delete all</em> button (hidden when the list is
      empty).
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Run</td><td>First 8 hex characters of the run id, monospace, linked to <code>/runs/{'{id}'}</code>.</td></tr>
        <tr><td>Workflow</td><td>Workflow name, resolved by a parallel fetch. Falls back to a truncated workflow id if the workflow has been deleted.</td></tr>
        <tr><td>Status</td><td>StatusBadge. Common values: <code>pending</code>, <code>running</code>, <code>completed</code>, <code>success</code>, <code>failed</code>, <code>failure</code>, <code>skipped</code>.</td></tr>
        <tr><td>Trigger</td><td>Source of the run (<code>manual</code>, <code>cron</code>, a scheduled trigger name). Dash when empty.</td></tr>
        <tr><td>Started</td><td>Absolute date-time (local timezone).</td></tr>
        <tr><td>Duration</td><td>Tabular-nums duration, live while the run is active.</td></tr>
        <tr>
          <td>Actions</td>
          <td>
            <em>Monitor</em> button (opens the live graph).
            For admins only: a trash-icon button that deletes this single run.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>Empty and loading states</h3>
    <ul>
      <li><strong>Loading</strong> — centered large spinner labeled <em>"Loading runs…"</em>.</li>
      <li><strong>Empty</strong> — an <em>Activity</em>-icon empty-state: "No runs yet — Run a workflow to see executions appear here."</li>
      <li><strong>Error</strong> — a dismissible error alert at the top of the page plus a toast.</li>
    </ul>

    <h3>Admin bulk delete</h3>
    <Callout tone="admin" title="Admin only">
      The <em>Delete all</em> header button is hidden unless your session role
      is <code>admin</code>. The backend also enforces this with
      <code>[Authorize(Policy = "Admin")]</code>.
    </Callout>
    <p>
      Clicking <em>Delete all</em> opens a confirm dialog warning that every
      run — and every child step run — will be soft-deleted in a
      single transaction. On success the list clears and a toast reports the
      number of rows affected.
    </p>
    <p>
      Individual row delete has the same semantics at single-row scope: the
      run and its step runs are marked <code>IsActive = false</code> so they
      vanish from every list/monitor query.
    </p>
  </section>

  <section>
    <h2>The detail page</h2>
    <p>
      Route: <code>/runs/{'{id}'}</code>. Opens via the run-id link in the list.
      Header includes a <em>Live</em> indicator (pulsing dot) while the run is
      still running plus a <em>Monitor</em> button that jumps to the DAG view.
    </p>

    <h3>Overview card</h3>
    <p>
      Four-column summary at the top:
    </p>
    <dl>
      <dt>Status</dt><dd>StatusBadge with the current state.</dd>
      <dt>Started / Finished</dt><dd>Absolute times, tabular-nums.</dd>
      <dt>Duration</dt><dd>Live-updating while <code>running</code>.</dd>
      <dt>Trigger</dt><dd>Source of the run.</dd>
    </dl>

    <h3>Data flow section</h3>
    <p>
      When the parent workflow is available, the page renders its DAG edges
      annotated with step statuses so you can follow which branch executed.
      Each edge shows:
    </p>
    <ul>
      <li>Source → target node ids.</li>
      <li>The edge type (<code>success</code>, <code>failure</code>, <code>always</code>).</li>
      <li>The status of the source step and the target step.</li>
    </ul>

    <h3>Step list</h3>
    <p>
      Each node that produced a step run is rendered as a card. Use the
      <strong>StepDetail</strong> expander to inspect:
    </p>
    <ul>
      <li><code>input_payload</code> — JSON the engine handed the node.</li>
      <li><code>output_payload</code> — what the node returned.</li>
      <li><code>logs</code> — captured stdout/stderr (scrollable block).</li>
      <li><code>error</code> — error message on failures (red block).</li>
      <li><code>worker_id</code> — which backend worker executed the step.</li>
    </ul>
    <p>
      A <em>Transform playground</em> button per step pre-loads the step's
      output into a JMESPath playground so you can prototype the next
      transform without leaving the page.
    </p>

    <h3>Live streaming</h3>
    <p>
      If the run is still <code>running</code> when the page loads, a WebSocket
      connects to <code>/api/run/{'{id}'}/stream</code>. Events update the UI
      in place:
    </p>
    <dl>
      <dt>run_status</dt><dd>Refreshes the run's status, started_at, completed_at.</dd>
      <dt>steps_update</dt><dd>Replaces the whole steps array (useful for reconnects).</dd>
      <dt>step_update</dt><dd>Upserts a single step row.</dd>
      <dt>run_completed</dt><dd>Updates status, closes the WebSocket, and re-fetches once for final consistency.</dd>
      <dt>error</dt><dd>Surfaces a stream error and stops streaming.</dd>
    </dl>
    <p>
      When the run is no longer live, the socket is closed; the detail page
      becomes static.
    </p>
  </section>

  <section>
    <h2>The monitor page</h2>
    <p>
      Route: <code>/runs/{'{id}'}/monitor</code>. The live execution view — what
      you see immediately after clicking <em>Run</em> in a workflow.
    </p>

    <h3>Header</h3>
    <p>
      Horizontal bar spanning the top. Three regions:
    </p>
    <ul>
      <li>
        <strong>Left</strong> — a back-link to <code>/runs</code>, a slash
        separator, the workflow name, and the run id (truncated).
      </li>
      <li>
        <strong>Center</strong> — the run's StatusBadge plus a progress bar
        (<code>completed_steps / total_steps</code>). The bar is red on
        failure, green on success, primary otherwise.
      </li>
      <li>
        <strong>Right</strong> — <em>Elapsed</em>, <em>Started</em>,
        <em>Finished</em> timestamps; a pulsing <em>Live</em> pill while
        streaming; a <em>Back to workflow</em> button (opens
        <code>/workflows/{'{workflow_id}'}</code>) and a <em>Details</em>
        shortcut to <code>/runs/{'{id}'}</code>.
      </li>
    </ul>

    <h3>Failure banner</h3>
    <p>
      When the run finishes as <code>failed</code> or <code>failure</code>, a
      red alert appears above the graph listing every distinct step error
      message (deduplicated). If no step reported a message, the banner says
      so and points you to the step detail for logs.
    </p>

    <h3>DAG (left pane)</h3>
    <p>
      A SvelteFlow canvas rendering the snapshot captured at enqueue time.
      Nodes are coloured by current status:
    </p>
    <dl>
      <dt>pending</dt><dd>Neutral border.</dd>
      <dt>running</dt><dd>Blue border with a soft ring-halo.</dd>
      <dt>completed / success</dt><dd>Green fill.</dd>
      <dt>failed / failure</dt><dd>Red fill.</dd>
      <dt>skipped</dt><dd>Dimmed neutral, 60% opacity.</dd>
    </dl>
    <p>
      Edges animate when either endpoint is running and are coloured green when
      a success has flowed through.
    </p>
    <p>
      The graph is not draggable on this page — interaction is limited to pan
      (drag empty canvas) and zoom (wheel).
    </p>

    <h3>Timeline (right pane, 400 px)</h3>
    <p>
      Scrollable list of every step, oldest first, with a header that shows
      <em>"N steps"</em>. Each row is a clickable expander:
    </p>
    <ul>
      <li>Collapsed: node id, status badge, started time, duration, truncated device id.</li>
      <li>Expanded: error block (red), input, output, logs, worker id. For <strong>failed</strong> steps a <em>Fix with AI</em> button appears; for any non-sentinel step an <em>Edit with AI</em> button too.</li>
    </ul>
    <p>
      <em>Fix with AI</em> bundles the failure context (node id, error, logs
      preview, service id, run id, workflow id) into a JSON blob and navigates
      to <code>/ai/chat?fix={'{json}'}</code>; the assistant auto-sends a
      diagnosis prompt on arrival. <em>Edit with AI</em> does the same but in
      edit mode, handing the assistant the current output as starting context.
    </p>

    <h3>Live updates</h3>
    <p>
      Same event set as the detail page, plus polling at 2 s as a fallback when
      the WebSocket fails. While streaming the timeline auto-scrolls to the
      bottom every tick so the newest step is always visible. Polling stops
      automatically when the run reaches a terminal state.
    </p>

    <h3>Empty / missing</h3>
    <p>
      If the workflow snapshot has zero nodes, the DAG area shows
      <em>"No workflow graph available"</em>. If no steps have been recorded yet,
      the timeline shows a small spinner when the run is running, or a
      <em>"No steps recorded"</em> message otherwise.
    </p>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — read-only list, detail, and monitor.</li>
      <li><strong>Operator</strong> — same as viewer. Runs are created by the engine, not by user writes.</li>
      <li><strong>Admin</strong> — can see and use the <em>Delete all</em> button plus the per-row delete icon.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — how runs get created.</li>
      <li><a href="/docs/schedules">Schedules</a> — cron-triggered runs.</li>
      <li><a href="/docs/ai/chat">AI chat</a> — the <em>Fix with AI</em> target.</li>
    </ul>
  </section>
</DocLayout>
