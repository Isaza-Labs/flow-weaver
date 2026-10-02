<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="QA lab"
  lead="Promotion readiness dashboard for workflows in the qa environment. Shows how big your QA fleet is, which workflows have been promoted, and whether each one has had a successful qa run in the last 48 hours — the gate for production promotion."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/qa"><code>/qa</code></a>.
    Sidebar → <strong>Operate</strong> → <strong>QA lab</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      FlowWeaver treats promotion as a deliberate, audited event. A workflow
      moves through three environments:
    </p>
    <ol>
      <li><strong>draft</strong> — edit freely; runs reach the devices and pools that allow <code>draft</code> (new devices and pools do by default).</li>
      <li><strong>qa</strong> — frozen graph; runs reach only the devices and pools that allow <code>qa</code> (off by default).</li>
      <li><strong>production</strong> — locked; runs reach only the devices and pools that allow <code>production</code>. Updates require clone → edit → promote again.</li>
    </ol>
    <p>
      The QA lab is where you verify that a promoted workflow works safely in
      a constrained environment before flipping it into production. The
      production promotion gate is explicit: there must be a completed
      <em>qa</em> run of the same workflow within the last 48 hours, plus a
      second approver on the promotion dialog. The 48-hour rule is the default
      policy <code>default.qa_to_production</code>; an admin can edit or replace
      it under <a href="/docs/policies">Policies</a>.
    </p>
  </section>

  <section>
    <h2>How a device or pool joins the lab</h2>
    <p>
      Devices and pools each declare which environments may dispatch to them,
      as three independent icon toggles — a hammer for <code>draft</code>, a
      flask for <code>qa</code>, a rocket for <code>production</code>:
    </p>
    <ul>
      <li>
        <strong>Devices</strong> — open <a href="/devices"><code>/devices</code></a>
        and light up the flask icon in the <em>Environments</em> column of the row.
      </li>
      <li>
        <strong>Device pools</strong> — open
        <a href="/device-pools"><code>/device-pools</code></a> and light up the
        flask icon on the pool card, or in the create/edit form.
      </li>
    </ul>
    <p>
      Any combination is valid, and the axis is symmetric: a device lit only
      for <code>production</code> is one no draft or qa run can ever reach.
      Turn all three off and the device is parked — no run can target it at all.
    </p>
    <Callout tone="info">
      Flagging is non-destructive and reversible. It only matters at dispatch
      time: the engine filters the resolved target list down to entries that
      allow the environment of the workflow being run. Pool flags apply on top
      of device flags — a run must be allowed by the pool <strong>and</strong>
      by the member it fans out to.
    </Callout>
    <Callout tone="warning">
      If <em>every</em> target you picked is filtered out, the run is refused
      with <code>409 no runnable targets in environment '…'</code> instead of
      starting. This is the usual reason a workflow that ran fine in
      <code>draft</code> finds no devices in <code>qa</code>: the selected
      devices allow draft but not qa. A <em>partial</em> match
      still runs — a mixed pool exposing only the members that allow the
      environment is by design. The run dialog and the schedule form grey out
      the devices that don't allow the workflow's environment, so the case is
      visible before you launch.
    </Callout>
  </section>

  <section>
    <h2>The dashboard</h2>
    <p>
      Loads once from <code>GET /api/qa/dashboard</code>. A <em>Refresh</em>
      button in the page header re-fetches on demand.
    </p>

    <h3>Top tiles</h3>
    <dl>
      <dt>Flagged devices</dt>
      <dd>Count of devices with <code>allow_qa: true</code>.</dd>
      <dt>Flagged pools</dt>
      <dd>Count of device pools with <code>allow_qa: true</code>.</dd>
      <dt>QA workflows</dt>
      <dd>Count of workflows currently sitting in the <code>qa</code> environment.</dd>
    </dl>

    <h3>Workflow table</h3>
    <p>
      One row per workflow in <code>qa</code>. If none, a flask-icon empty
      state tells you to promote a draft to see it here.
    </p>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Workflow</td><td>Name of the QA workflow.</td></tr>
        <tr><td>Version</td><td>Current QA version, monospace.</td></tr>
        <tr>
          <td>Last run</td>
          <td>
            A badge with the status of the most recent run. If the
            workflow has never been run, the cell shows <em>"never run"</em>.
          </td>
        </tr>
        <tr>
          <td>Completed</td>
          <td>Date-time of the most recent completion, or <code>—</code> when no run has finished.</td>
        </tr>
        <tr>
          <td>Promotion</td>
          <td>
            A badge summarising the production gate:
            <ul>
              <li><strong>Ready</strong> (green check) — there is a successful qa run within the 48-hour window; the production promote dialog will accept it.</li>
              <li><strong>Needs qa run (&lt; 48h)</strong> (neutral clock) — the last qualifying run is too old, failed, or absent.</li>
            </ul>
          </td>
        </tr>
        <tr><td>Open</td><td>A <em>Ghost</em> button that jumps to the workflow editor.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Running a QA validation</h2>
    <ol>
      <li>From the QA lab page, click <em>Open</em> on the workflow you want to validate.</li>
      <li>In the editor, click the green <em>Run</em> button in the toolbar.</li>
      <li>
        In the <em>Run</em> dialog, pick one or more devices that allow
        <code>qa</code>. Devices that don't are greyed out, and the engine drops
        any that slip through.
      </li>
      <li>Fire the run. The monitor page shows the live execution.</li>
      <li>
        Once it completes successfully, the QA lab dashboard refreshes the
        workflow's <em>Promotion</em> column to <strong>Ready</strong>.
      </li>
    </ol>
  </section>

  <section>
    <h2>Promoting to production</h2>
    <ol>
      <li>From the workflow list, switch to the <em>QA</em> tab and find the workflow (the qa copy created when the draft was promoted).</li>
      <li>Click <em>Promote</em>. The dialog infers the next environment: since this workflow is already in qa, the target is <code>production</code>.</li>
      <li>Write a <em>Change summary</em> describing what is shipping.</li>
      <li>Enter an <em>Approved by</em> name: the second reviewer. The server refuses the promotion if it is the username of the user running it. This is the second signature.</li>
      <li>Submit. If the 48-hour QA run gate is not satisfied, the backend rejects the call and the dialog shows the error.</li>
    </ol>
  </section>

  <section>
    <h2>Why the 48-hour gate?</h2>
    <p>
      Production promotions are meant to be deliberate — an approver should
      know the change has at least been observed running cleanly in QA
      recently. 48 hours is the compromise that accommodates "I ran it
      Friday, shipped it Monday morning" without letting month-old test
      evidence carry forward indefinitely.
    </p>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — see the dashboard; cannot trigger runs.</li>
      <li><strong>Operator</strong> — same as viewer, plus can run a QA workflow from the editor and promote it to production.</li>
      <li><strong>Admin</strong> — same as operator. Promotion to production still requires the <em>Approved by</em> second-signer field, which can't be the admin's own username.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — where promotion and rollback dialogs live.</li>
      <li><a href="/docs/devices">Devices</a> — flagging individual targets as QA.</li>
      <li><a href="/docs/device-pools">Device pools</a> — flagging groups.</li>
    </ul>
  </section>
</DocLayout>
