<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Service-level objectives"
  lead="Four platform-wide indicators, each compared against a built-in target over a window you choose: run latency, run error rate, job throughput and promotion latency. A daily sweep writes every breach to the audit log."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/slo"><code>/admin/slo</code></a>. From the UI: user menu →
    <strong>Admin dashboard</strong> → <strong>SLO</strong> card.
  </Callout>
  <Callout tone="admin" title="Admin only">
    Requires the <code>admin</code> role. Other users are redirected to the dashboard, and the
    API (<code>GET /api/admin/metrics/slo</code>) checks the role itself, so granting the
    <code>metrics.read</code> capability to a non-admin does not open it.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      Use this page to see whether the platform is healthy overall: are runs finishing in
      reasonable time, are too many failing, is work flowing, and do changes reach QA and
      production at a reasonable pace. It is a starting point for investigation. A red card
      is not an incident on its own.
    </p>
    <p>
      The numbers cover the whole installation. There is no breakdown per workflow, device or
      user.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>You need the <code>admin</code> role.</li>
      <li>The indicators only mean something once runs, jobs and promotions exist in the window. On a fresh or idle installation, expect grey cards and a red throughput card.</li>
      <li>All times are UTC.</li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>
    <dl>
      <dt>Window</dt>
      <dd>
        <em>N days</em> means today (UTC) plus the previous <em>N−1</em> whole days. It starts at
        00:00 UTC on the first day, shown as <em>Window starts YYYY-MM-DD UTC</em>. The server
        works in a range of 1 to 90 days, and it <strong>clamps</strong> rather than rejects:
        a request for 0 or a negative number is computed over 1 day, and anything above 90
        over 90 days. The response reports the window it actually used, so read
        <em>Window starts</em> rather than assuming your number was honored.
      </dd>
      <dt>Target</dt>
      <dd>
        A fixed, built-in value per indicator. It cannot be changed from the UI or from
        configuration.
      </dd>
      <dt>Direction</dt>
      <dd>
        Each indicator is either <em>lower is better</em> (target shown as <code>≤</code>) or
        <em>higher is better</em> (<code>≥</code>).
      </dd>
      <dt>No data</dt>
      <dd>
        An indicator with nothing to measure has no value. It shows <code>—</code> with a grey
        dot and never counts as a breach.
      </dd>
    </dl>
  </section>

  <section>
    <h2>The indicators</h2>
    <table>
      <thead><tr><th>Card label</th><th>Key</th><th>Unit</th><th>Target</th></tr></thead>
      <tbody>
        <tr><td>Run latency (p95)</td><td><code>run_latency_p95_seconds</code></td><td>seconds</td><td>≤ 600 s (shown as <em>≤ 10.0 min</em>)</td></tr>
        <tr><td>Run error rate</td><td><code>error_rate</code></td><td>ratio, shown as %</td><td>≤ 5.00%</td></tr>
        <tr><td>Throughput</td><td><code>throughput_jobs_per_hour</code></td><td>jobs/hr</td><td>≥ 5.00 jobs/hr</td></tr>
        <tr><td>Promotion latency (median)</td><td><code>promotion_latency_seconds</code></td><td>seconds</td><td>≤ 86 400 s (shown as <em>≤ 24.0 h</em>)</td></tr>
      </tbody>
    </table>

    <h3>Which runs count</h3>
    <p>
      The two run indicators use runs <strong>created</strong> inside the window that have both
      a start and a completion time. Runs still pending or running are left out until they
      finish. Every trigger counts, including schedules, webhooks and subflow child runs. A
      parent and its subflow child therefore count as two runs.
    </p>
    <Callout tone="info" title="Soft-deleted rows drop out of all three inputs">
      Each of the three queries behind the four cards — finished runs, completed queue jobs
      and promoted workflow copies — only reads rows that are still active. Deleting a run,
      a job or a workflow in FlowWeaver deactivates the row rather than erasing it, and from
      that moment it stops counting, including in windows that have already passed. So the
      same window can return different numbers before and after a clean-up.
    </Callout>

    <h3>Run latency (p95)</h3>
    <p>
      <strong>Definition:</strong> the 95th percentile (nearest rank) of
      <em>completed at − started at</em> across those runs, whatever their status. Failed and
      cancelled runs count too.
    </p>
    <p>
      <strong>Display:</strong> seconds below one minute, minutes up to an hour, hours above.
    </p>
    <p>
      <strong>Reading it:</strong> 95% of runs finished at least this fast. One long nightly
      backup across hundreds of devices can dominate p95 on a quiet installation. Check which
      workflows are slow before calling it a regression.
    </p>

    <h3>Run error rate</h3>
    <p>
      <strong>Definition:</strong> runs with status <code>failed</code> divided by all counted
      runs <em>except</em> cancelled ones. A cancellation is not an error.
    </p>
    <p>
      <strong>Reading it:</strong> 3.20% means about 1 in 31 finished runs failed. On a small
      sample, one failure can push this past 5%, so compare with the absolute numbers in
      <a href="/runs">Runs</a>. No value means no finished, non-cancelled run in the window.
    </p>

    <h3>Throughput</h3>
    <p>
      <strong>Definition:</strong> jobs in the work queue with status <code>completed</code>
      and created in the window, divided by <em>days × 24</em>.
    </p>
    <p>
      <strong>Reading it:</strong> this counts <em>queue jobs</em>, not runs. Each run
      contributes an orchestration job plus its step jobs, so a busy workflow with many steps
      or devices raises the number more than a simple one. The divisor counts today as a full
      day, so early in the UTC day the value reads low, most noticeably with the 1-day window.
      This indicator always has a value: with no completed jobs it is <code>0.00 jobs/hr</code>,
      which is red.
    </p>

    <h3>Promotion latency (median)</h3>
    <p>
      <strong>Definition:</strong> for each workflow copy created in the window in
      <code>qa</code> or <code>production</code> by a promotion, the time between the creation
      of the row it was promoted <em>from</em> and the creation of the copy. The card shows the
      median; with an even count it takes the upper of the two middle values.
    </p>
    <p>
      <strong>Reading it:</strong> for draft → QA, the clock starts when the draft workflow was
      first created, not at its last edit. A long-lived draft promoted again months later
      therefore shows a very large latency. For QA → production, the clock starts when the QA
      copy was created. Promotions whose source row no longer exists are left out; a source
      that was merely deleted in the UI still supplies its timestamp, because the lookup of
      the source row is the one query here that does not skip deactivated rows. No value
      means no promotion in the window.
    </p>
  </section>

  <section>
    <h2>The screen and its fields</h2>
    <dl>
      <dt>Refresh</dt><dd>Reloads with the current window.</dd>
      <dt>Window (days)</dt>
      <dd>
        <em>1 day</em>, <em>7 days</em> (default), <em>30 days</em>, <em>90 days</em>. Changing it
        does not reload; click <strong>Apply</strong>.
      </dd>
      <dt>Window starts … UTC</dt><dd>The first day included, as returned by the server.</dd>
      <dt>Cards</dt><dd>One per indicator: colored dot, label, value, and <em>target ≤ …</em> or <em>target ≥ …</em>.</dd>
      <dt>How these are computed</dt><dd>A short reference card that summarizes the four definitions above.</dd>
    </dl>

    <h3>Status colors</h3>
    <table>
      <thead><tr><th>Dot</th><th>Lower is better</th><th>Higher is better</th></tr></thead>
      <tbody>
        <tr><td>Green</td><td>value ≤ target</td><td>value ≥ target</td></tr>
        <tr><td>Yellow</td><td>value ≤ 1.25 × target</td><td>value ≥ 0.75 × target</td></tr>
        <tr><td>Red</td><td>value &gt; 1.25 × target</td><td>value &lt; 0.75 × target</td></tr>
        <tr><td>Grey</td><td>no value</td><td>no value</td></tr>
      </tbody>
    </table>
    <p>
      Examples: an error rate of 6% (target 5%) is yellow; 7% is red. A throughput of 4 jobs/hr
      (target 5) is yellow; 3 is red.
    </p>
  </section>

  <section>
    <h2>Main procedure</h2>
    <ol>
      <li>Open <code>/admin/slo</code>. The 7-day view loads.</li>
      <li>Scan the dots. Grey means no data; check that the installation actually did work in the window.</li>
      <li>For a yellow or red card, switch to <em>1 day</em> and <strong>Apply</strong> to see whether the problem is current, then to <em>30 days</em> to see whether it is a trend.</li>
      <li>Investigate with the relevant page (see Troubleshooting).</li>
      <li>To see past breaches, open the <a href="/docs/admin/audit">audit log</a> and filter <strong>Entity type</strong> <code>slo</code>.</li>
    </ol>

    <h3>Daily breach sweep</h3>
    <p>
      About a minute after the API server starts, and then every 24 hours, FlowWeaver computes
      the indicators over <strong>7 days</strong>. For every indicator strictly worse than its
      target, it writes one audit event:
    </p>
    <ul>
      <li>Action <code>slo.breach.&lt;key&gt;</code>, for example <code>slo.breach.error_rate</code>.</li>
      <li>Entity type <code>slo</code>, actor <code>slo-watcher</code>.</li>
      <li>Details: key, label, unit, value, target, direction, window days and window start.</li>
    </ul>
    <p>
      A <em>yellow</em> card is already a breach for the sweep; the tolerance band is only a
      display aid. No email, chat or pager notification is sent. Build alerting on top of the
      audit events if you need it. The sweep runs only in the API server, not in the worker.
    </p>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Situation:</strong> the 7-day view shows <em>Run error rate</em> at
      <code>12.50%</code> with a red dot (target ≤ 5.00%).
    </p>
    <ol>
      <li>Switch to <em>1 day</em> → <strong>Apply</strong>. It shows <code>0.00%</code> (green), so the failures happened earlier in the week.</li>
      <li>Open <a href="/runs"><code>/runs</code></a>, filter by status <em>failed</em>, and look at the affected days. Suppose most failures come from one scheduled workflow.</li>
      <li>Open one of those runs and read the failing step's error. Suppose it is an authentication failure from a rotated credential that has since been fixed.</li>
      <li>Check the <a href="/docs/admin/audit">audit log</a> for <code>slo.breach.error_rate</code> rows to see on which days the sweep flagged it.</li>
    </ol>
    <p>
      <strong>Expected result:</strong> the 7-day value falls as the failing days leave the
      window. No action is needed on this page.
    </p>
    <p>
      <strong>Common error:</strong> <em>Couldn't load SLOs</em> with a permission error after
      your role was changed. <strong>Recovery:</strong> confirm with another admin that your
      account still has the <code>admin</code> role, sign in again, then click
      <strong>Retry</strong>.
    </p>
  </section>

  <section>
    <h2>Relation to runs and traces</h2>
    <ul>
      <li>The run indicators come straight from the run records you see in <a href="/docs/runs">Runs</a>. Anything that changes run history, such as pruning runs, changes the indicators for past windows.</li>
      <li>Throughput comes from the job queue, not from runs. Queue health (pending or stuck jobs) is not shown here.</li>
      <li>Traces are not used in the calculation. Use <a href="/docs/admin/traces">Traces</a> with <em>Slower than</em> to find slow calls inside a slow run.</li>
      <li>Breaches appear in the <a href="/docs/admin/audit">audit log</a>, not in traces.</li>
    </ul>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li>Page and API: <code>admin</code> role only. The page is read-only and has no actions that change data.</li>
      <li>The values are aggregates and contain no workflow names, device names or user data.</li>
      <li>Breach audit events have no user; the actor is <code>slo-watcher</code>.</li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li><strong>Loading:</strong> a spinner labeled <em>Loading SLOs…</em>.</li>
      <li><strong>No data for an indicator:</strong> value <code>—</code>, grey dot. The target is still shown.</li>
      <li><strong>Nothing ran at all:</strong> latency, error rate and promotion latency are grey; throughput is <code>0.00 jobs/hr</code> and red.</li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>What you see</th><th>Cause</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr><td>Red alert <em>Couldn't load SLOs</em> plus toast <em>Failed to load SLOs</em></td><td>Request failed: network, server error, or missing admin role.</td><td>Click <strong>Retry</strong>. If it persists, check the backend logs and your role.</td></tr>
        <tr><td>Redirected to the dashboard</td><td>Your session is not an admin.</td><td>Ask an admin.</td></tr>
        <tr><td>Rate-limit error after repeated refreshes</td><td>The endpoint uses the read-heavy rate limit.</td><td>Wait a moment and retry.</td></tr>
      </tbody>
    </table>

    <h3>Troubleshooting by indicator</h3>
    <dl>
      <dt>Run latency red</dt>
      <dd>Look in <a href="/runs">Runs</a> for long runs in the window. Open the run monitor to find the slow step (often SSH timeouts or large fan-outs), and use Traces with <em>Slower than</em> for slow platform calls.</dd>
      <dt>Error rate red</dt>
      <dd>Filter Runs by failed status and group the failures by workflow. A single broken schedule is the usual cause.</dd>
      <dt>Throughput red</dt>
      <dd>First check whether the installation was simply idle. If work was expected, check that the worker is running and that jobs are completing.</dd>
      <dt>Promotion latency red</dt>
      <dd>Usually old drafts being promoted, which is expected given how the clock starts. Look at recent promotions before treating it as a process problem.</dd>
    </dl>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>Targets are fixed in code and cannot be configured.</li>
      <li>There is no per-workflow view and no error budget: the four indicators are platform-wide, and nothing on this page or on the admin dashboard breaks them down by workflow.</li>
      <li>No history chart; each load is a single snapshot.</li>
      <li>The breach sweep always uses 7 days, regardless of the window selected on the page.</li>
      <li>Throughput has no "no data" state, so idle installations show red and produce a daily <code>slo.breach.throughput_jobs_per_hour</code> audit event.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/runs">Runs</a> — the data behind latency and error rate.</li>
      <li><a href="/docs/admin/audit">Audit log</a> — where breaches are recorded.</li>
      <li><a href="/docs/admin/traces">Traces</a> — finding slow calls.</li>
      <li><a href="/docs/workflows">Workflows</a> — promotion, the source of promotion latency.</li>
    </ul>
  </section>
</DocLayout>
