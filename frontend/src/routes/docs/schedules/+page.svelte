<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Schedules"
  lead="Cron-driven triggers that invoke workflows automatically. The top-level page lists every schedule across every workflow; the calendar view renders upcoming fires for the visible month."
>
  <Callout tone="where" title="Where to find it">
    List: <a href="/schedules"><code>/schedules</code></a> ·
    Calendar: <code>/schedules/calendar</code>.
    Per-workflow: <code>/workflows/{'{id}'}/schedules</code>.
    Sidebar → <strong>Operate</strong> → <strong>Schedules</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A schedule is one kind of <strong>workflow trigger</strong> — a cron
      expression plus a timezone that fires the workflow on a repeating
      interval. Other trigger types live on the workflow's
      <a href="/workflows"><em>Triggers</em></a> sub-page: <strong>webhook</strong>
      triggers expose a public, HMAC-signed <code>POST</code> endpoint
      (<code>/api/webhooks/workflow/&#123;id&#125;</code>) whose body becomes the run
      input; this page filters to <code>type === 'schedule'</code> only.
    </p>
    <Callout tone="warning" title="Webhooks don't pick their own devices by default">
      A webhook caller proves it holds the trigger's secret — not that it is a
      user with permission to touch a given device. So
      <code>target_devices</code> / <code>target_pools</code> in the body are
      <strong>ignored</strong> unless the trigger sets
      <code>allow_target_override</code>, and the run fires against the devices
      configured on the trigger. Turn the flag on and the body can
      <em>narrow</em> that list; it can never extend it. Without this, a leaked
      webhook secret would mean "run this workflow against any device in the
      inventory", production included.
    </Callout>
    <p>
      Every schedule is attached to exactly one workflow. Create and edit them
      from the workflow's <em>Schedules</em> sub-page
      (<code>/workflows/{'{id}'}/schedules</code>); the top-level
      <code>/schedules</code> surface is the global index with filters,
      sorting, and quick toggles.
    </p>
  </section>

  <section>
    <h2>The list page</h2>
    <p>
      Route: <code>/schedules</code>. Header action: <em>Calendar view</em> —
      a shortcut to the monthly grid.
    </p>

    <h3>Stat cards</h3>
    <dl>
      <dt>Total</dt><dd>Number of schedule triggers.</dd>
      <dt>Enabled</dt><dd>Subset currently armed (green).</dd>
      <dt>Disabled</dt><dd>Subset paused (yellow).</dd>
    </dl>

    <h3>Filters</h3>
    <ul>
      <li>
        <strong>Search input</strong> — client-side filter matching on the
        schedule name, workflow name, cron expression, and timezone.
      </li>
      <li>
        <strong>Status select</strong> — <code>All</code> / <code>Enabled</code>
        / <code>Disabled</code>, each with its current count.
      </li>
      <li>
        <strong>Refresh</strong> — re-fetches triggers and workflow names.
      </li>
    </ul>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Workflow</td><td>Workflow name, linked to <code>/workflows/{'{id}'}</code>. Sortable (A→Z by default; click to flip).</td></tr>
        <tr><td>Schedule</td><td>Trigger display name and, below it, the raw cron expression in monospace. Sortable by name.</td></tr>
        <tr>
          <td>Repeat</td>
          <td>
            Human-friendly summary of the cron expression. The UI recognises
            common patterns and renders them as:
            <ul>
              <li><em>Hourly :MM</em> for <code>MM * * * *</code></li>
              <li><em>Weekdays HH:MM</em> for <code>MM HH * * 1-5</code></li>
              <li><em>Mon HH:MM</em> (or any weekday) for <code>MM HH * * d</code></li>
              <li><em>Day D HH:MM</em> for monthly fires</li>
              <li><em>Daily HH:MM</em> for everyday fires</li>
              <li>Raw cron otherwise.</li>
            </ul>
          </td>
        </tr>
        <tr><td>Timezone</td><td>Monospace timezone id, defaults to <code>UTC</code>.</td></tr>
        <tr><td>Next run</td><td>Absolute date-time of the next fire. Sortable.</td></tr>
        <tr><td>Last run</td><td>When the previous fire completed, if any.</td></tr>
        <tr><td>Status</td><td>Badge. <em>disabled</em> (yellow) wins over everything else. Otherwise the last run's status, or <em>pending</em> if it has never run.</td></tr>
        <tr>
          <td>Actions</td>
          <td>
            <em>Open</em> (goes to the workflow's schedules page), a power
            toggle to enable/disable in place, and a red delete icon.
          </td>
        </tr>
      </tbody>
    </table>

    <h3>Sorting</h3>
    <p>
      Click a sortable column header to sort by that key. Clicking the same
      header again flips the direction. An up/down arrow indicates the active
      column.
    </p>

    <h3>Toggle and delete</h3>
    <p>
      The power button flips <code>enabled</code> via a PUT and refreshes the
      list. Delete asks for confirmation — the message includes the workflow
      name so you can sanity-check before destroying a production schedule.
    </p>

    <h3>Empty states</h3>
    <ul>
      <li>No schedules at all → <em>"No schedules yet — Add one from any workflow page: Workflows → pick a workflow → Schedules."</em></li>
      <li>Filters exclude everything → <em>"No schedules match the current filter."</em></li>
    </ul>
  </section>

  <section>
    <h2>The calendar page</h2>
    <p>
      Route: <code>/schedules/calendar</code>. A monthly 7×6 grid showing every
      scheduled fire in your local timezone. The heading shows the visible
      month, with arrow buttons to step backward / forward and a <em>Today</em>
      button to jump back to the current month.
    </p>

    <h3>How fires are computed</h3>
    <p>
      Every <strong>enabled schedule</strong> with a cron expression is
      iterated client-side with <a
        href="https://github.com/hexagon/croner" target="_blank" rel="noreferrer"
      >croner</a>, honouring its stored timezone. The view pre-computes up to
      500 future fires per schedule within the visible grid (six weeks), sorts
      them chronologically, and drops them into each day's cell.
    </p>

    <h3>Day cells</h3>
    <ul>
      <li><strong>Today</strong> — primary-colour inner ring.</li>
      <li><strong>Selected</strong> — primary-tint background.</li>
      <li><strong>Out of month</strong> — dimmed to 50 % opacity.</li>
      <li>
        <strong>Content</strong> — day number, a small count badge when there
        are fires, and the first three fires (time + workflow name). A
        <em>+N more</em> hint appears when there are more.
      </li>
    </ul>

    <h3>Day detail</h3>
    <p>
      Clicking any cell opens a card beneath the grid with the long-format
      date and a list of that day's fires:
    </p>
    <ul>
      <li><strong>Time</strong> — local time of the fire, monospace.</li>
      <li><strong>Workflow</strong> — link to the workflow editor.</li>
      <li><strong>Schedule name</strong> — link to the edit page for that trigger.</li>
      <li><strong>Cron expression</strong> and <strong>timezone</strong> — raw, monospace.</li>
    </ul>
    <p>
      The card has a close icon on the right; you can also click a different
      day to swap the panel contents.
    </p>

    <h3>Summary row</h3>
    <p>
      Above the grid the page shows the total fires in the visible month and
      the number of active schedules contributing to them. Useful to spot a
      month where the automation load is abnormally heavy or light.
    </p>
  </section>

  <section>
    <h2>Creating and editing schedules</h2>
    <p>
      Creation is not on this page — you do it from the workflow's own
      <em>Schedules</em> sub-page:
    </p>
    <ol>
      <li>Open the workflow editor at <code>/workflows/{'{id}'}</code>.</li>
      <li>Click the <em>Schedules</em> button in the toolbar. Opens <code>/workflows/{'{id}'}/schedules</code>.</li>
      <li>Click <em>Add schedule</em> to reach the new form, or <em>Edit</em> on an existing row.</li>
    </ol>
    <p>
      The form accepts:
    </p>
    <dl>
      <dt>Name</dt><dd>Human label for the schedule.</dd>
      <dt>Cron expression</dt><dd>Standard 5-field format (<code>m h dom mon dow</code>).</dd>
      <dt>Timezone</dt><dd>IANA timezone id. Defaults to <code>UTC</code>.</dd>
      <dt>Enabled</dt><dd>Checkbox. Disabled schedules stay in the list but never fire.</dd>
      <dt>Input payload</dt><dd>Optional JSON passed to the workflow at each fire.</dd>
      <dt>Target devices / pools</dt><dd>Optional. Same semantics as the manual <em>Run</em> dialog.</dd>
    </dl>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — can list and browse the calendar.</li>
      <li><strong>Operator</strong> — full CRUD on schedules; toggle, delete, edit.</li>
      <li><strong>Admin</strong> — same as operator.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — where schedules are created.</li>
      <li><a href="/docs/runs">Runs</a> — every fire produces one.</li>
    </ul>
  </section>
</DocLayout>
