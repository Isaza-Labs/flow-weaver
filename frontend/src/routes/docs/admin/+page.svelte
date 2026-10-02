<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Admin dashboard"
  lead="System health at a glance: job queue depth, runs activity over time, and auth activity with lockouts, plus the Tools cards that open the administration screens. Auto-refreshes every 30 seconds."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin"><code>/admin</code></a>.
    User menu (avatar bottom-left) → <em>Admin dashboard</em>.
  </Callout>
  <Callout tone="admin" title="Admin only">
    The entire <code>/admin/*</code> tree requires the <code>admin</code> role.
    The browser sends non-admins back to <code>/</code>, and the server
    refuses the admin endpoints behind these pages for anyone else.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      A single landing page for admins who care about health, throughput,
      and security signals. From top to bottom:
    </p>
    <ol>
      <li><strong>Queue</strong> — counts of queued jobs by state.</li>
      <li><strong>Tools</strong> — cards that open the administration screens.</li>
      <li><strong>Runs activity</strong> and <strong>Top failing</strong> — runs per day by status, next to the workflows that fail most.</li>
      <li><strong>Sign-in activity</strong> — stacked bar chart of login successes, failures, lockouts.</li>
    </ol>
  </section>

  <section>
    <h2>Header</h2>
    <dl>
      <dt>Window select</dt>
      <dd>
        <em>Last 7 days</em>, <em>Last 14 days</em> or <em>Last 30 days</em>.
        Both charts and the <em>Top failing</em> list respect this window.
      </dd>
      <dt>Live / Paused</dt>
      <dd>
        Toggle. Default <em>Live</em>. Polling interval is 30 seconds.
        Pausing is handy when you're reading the charts and don't want them
        refreshed out from under your cursor.
      </dd>
      <dt>Updated</dt>
      <dd>Tiny timestamp of the last successful fetch.</dd>
    </dl>
  </section>

  <section>
    <h2>Queue</h2>
    <p>
      Four counters, always shown: <em>Pending</em>, <em>Claimed</em>,
      <em>Completed</em> and <em>Failed</em>. They count jobs in the work
      queue, not runs. A <em>Pending</em> count that keeps growing while
      <em>Claimed</em> stays at zero usually means no worker is picking up
      jobs.
    </p>
  </section>

  <section id="tools">
    <h2>Tools</h2>
    <p>Each card opens an administration screen:</p>
    <table>
      <thead><tr><th>Card</th><th>Opens</th></tr></thead>
      <tbody>
        <tr><td>Users</td><td><a href="/admin/users"><code>/admin/users</code></a> — see <a href="/docs/admin/users">Users</a>.</td></tr>
        <tr><td>Audit log</td><td><a href="/admin/audit"><code>/admin/audit</code></a> — see <a href="/docs/admin/audit">Audit log</a>.</td></tr>
        <tr><td>Traces</td><td><a href="/admin/traces"><code>/admin/traces</code></a> — see <a href="/docs/admin/traces">Traces</a>.</td></tr>
        <tr><td>Artifacts</td><td><a href="/admin/artifacts"><code>/admin/artifacts</code></a> — see <a href="/docs/admin/artifacts">Artifacts</a>.</td></tr>
        <tr><td>SLO</td><td><a href="/admin/slo"><code>/admin/slo</code></a> — see <a href="/docs/admin/slo">Service-level objectives</a>.</td></tr>
        <tr><td>Settings</td><td><a href="/admin/settings"><code>/admin/settings</code></a> — see <a href="/docs/admin/settings">Settings</a>.</td></tr>
        <tr><td>Themes</td><td><a href="/themes"><code>/themes</code></a> — see <a href="/docs/themes">Themes</a>.</td></tr>
      </tbody>
    </table>
    <p>
      SLO and Settings are only reachable from here (or by URL); the sidebar
      has no entry for them.
    </p>
    <Callout tone="info" title="Secrets">
      Secrets (<a href="/admin/secrets"><code>/admin/secrets</code></a>) has
      no card here; open it from the AI hub's <em>Secrets (admin)</em> link or
      by URL. See <a href="/docs/admin/secrets">Secrets</a>.
    </Callout>
  </section>

  <section>
    <h2>Runs activity and Top failing</h2>
    <p>
      <em>Runs activity</em> is a stacked bar chart, one bar per day in the
      selected window. Series order (bottom → top): <code>completed</code>,
      <code>failed</code>, <code>running</code>, <code>pending</code>. The
      header shows the aggregate totals for the entire window so the eye has
      a number to anchor the visual.
    </p>
    <p>
      <em>Top failing</em> lists the workflows with the most failed runs in
      the window, each linked to its editor. With none it shows
      <em>No failures in this window.</em>
    </p>
  </section>

  <section>
    <h2>Sign-in activity chart</h2>
    <p>
      Second stacked bar chart. It plots only <code>login_success</code>,
      <code>login_failure</code> and <code>lockout</code>; the other auth
      events (logout, password change, refresh, token revocation) are in the
      Auth tab of the <a href="/docs/admin/audit">Audit log</a>. Lockouts are purple (distinct from the red
      of login_failure) so accumulated failed attempts don't mask the
      escalation event that matters more.
    </p>
    <Callout tone="warning" title="Escalation signal">
      A rising <code>lockout</code> series without corresponding
      <code>login_success</code> recovery is a strong signal that a user is
      locked out in production, a bot is probing credentials, or a provider
      rotation broke an automated login. Follow up via the audit log at
      <a href="/admin/audit"><code>/admin/audit</code></a> (Auth tab).
    </Callout>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/admin/users">Users</a> — manage accounts that emit auth events.</li>
      <li><a href="/docs/admin/audit">Audit log</a> — drill into the raw events.</li>
      <li><a href="/docs/admin/traces">Traces</a> — application-level action trail.</li>
      <li><a href="/docs/admin/slo">Service-level objectives</a> — the SLO card under Tools.</li>
    </ul>
  </section>
</DocLayout>
