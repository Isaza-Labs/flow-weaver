<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Admin dashboard"
  lead="System health at a glance: job queue depth, runs activity over time, and auth activity with lockouts. Auto-refreshes every 30 seconds."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin"><code>/admin</code></a>.
    User menu (avatar bottom-left) → <em>Admin dashboard</em>.
  </Callout>
  <Callout tone="admin" title="Admin only">
    The entire <code>/admin/*</code> tree requires the <code>admin</code> role.
    The client-side guard in <code>+layout.svelte</code> bounces non-admins
    to <code>/</code>; every admin endpoint is also gated server-side with
    <code>[Authorize(Policy = "Admin")]</code>.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      A single landing page for operators who care about health,
      throughput, and security signals. Three major sections:
    </p>
    <ol>
      <li><strong>Job queue</strong> — live count of queued vs running jobs.</li>
      <li><strong>Run activity</strong> — stacked bar chart of runs per day by status.</li>
      <li><strong>Auth activity</strong> — stacked bar chart of login successes, failures, lockouts.</li>
    </ol>
  </section>

  <section>
    <h2>Header</h2>
    <dl>
      <dt>Window select</dt>
      <dd>
        Choose <code>7</code>, <code>14</code>, or <code>30</code> days. Both
        charts respect this window.
      </dd>
      <dt>Auto-refresh pause/play</dt>
      <dd>
        Toggle. Default on. Polling interval is 30 seconds. Pausing is handy
        when you're reading the charts and don't want them refreshed out
        from under your cursor.
      </dd>
      <dt>Last refreshed</dt>
      <dd>Tiny timestamp of the last successful fetch.</dd>
    </dl>
  </section>

  <section>
    <h2>Job queue card</h2>
    <p>
      One pill per distinct job status currently in the queue plus its count.
      Hidden when the queue is empty so the dashboard stays clean when
      things are idle. Uses the same colour palette as <code>StatusBadge</code>:
    </p>
    <ul>
      <li><strong>completed</strong> — green, base of the bar stack.</li>
      <li><strong>failed</strong> — red, top of the stack so spikes stick out.</li>
      <li><strong>running</strong> — blue.</li>
      <li><strong>pending</strong> — amber.</li>
    </ul>
  </section>

  <section>
    <h2>Run activity chart</h2>
    <p>
      Stacked bar chart, one bar per day in the selected window. Series
      order (bottom → top): <code>completed</code>, <code>failed</code>,
      <code>running</code>, <code>pending</code>. The header shows the
      aggregate totals for the entire window so the eye has a number to
      anchor the visual.
    </p>
  </section>

  <section>
    <h2>Auth activity chart</h2>
    <p>
      Second stacked bar chart. Series include <code>login_success</code>,
      <code>login_failure</code>, <code>lockout</code>, <code>logout</code>,
      <code>password_change</code>, <code>refresh</code>, and
      <code>token_revoked</code>. Lockouts are purple (distinct from the red
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
    </ul>
  </section>
</DocLayout>
