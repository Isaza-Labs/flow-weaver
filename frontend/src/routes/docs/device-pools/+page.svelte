<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Device pools"
  lead="Named groups of devices that workflows target together. Pools let a workflow say 'run on the core routers' instead of enumerating devices by id, and they're where environment scoping is enforced alongside the per-device flags."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/device-pools"><code>/device-pools</code></a>.
    Sidebar → <strong>Operate</strong> → <strong>Device pools</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A pool is a persistent bag of device ids. A workflow's <em>Run</em>
      dialog accepts a list of target devices and/or target pools; at
      execution time the engine expands every pool into its members and
      unions the result.
    </p>
    <p>
      Each pool has:
    </p>
    <dl>
      <dt>Name</dt><dd>A short, recognisable label (e.g. <code>core-routers</code>).</dd>
      <dt>Description</dt><dd>Optional free-text context.</dd>
      <dt>Static members</dt><dd>The list of device ids that belong to this pool.</dd>
      <dt>Environments</dt><dd>Three flags — <em>draft</em> (hammer), <em>qa</em> (flask), <em>production</em> (rocket) — declaring which environments may target this pool.</dd>
    </dl>
    <Callout tone="info">
      The backend model also supports <em>filter rules</em> for dynamically
      resolved membership, but the current UI only exposes the static list.
      Dynamic pools are reserved for a future release.
    </Callout>
  </section>

  <section>
    <h2>List layout</h2>
    <p>
      A single header (with a <em>New pool</em> action) followed by one card
      per pool. Large spinner while loading; an empty state with its own
      <em>Add pool</em> button when there is nothing yet.
    </p>

    <h3>Pool card</h3>
    <p>
      Each row shows, from left to right:
    </p>
    <ul>
      <li>The pool name in bold.</li>
      <li>A yellow <em>parked</em> badge when no environment is allowed — otherwise the lit icon toggles on the right carry that state.</li>
      <li>A neutral <em>{'{N}'} device(s)</em> badge with the member count.</li>
      <li>The description on a second line, if present.</li>
    </ul>
    <p>
      The right side carries the three environment icon toggles, a pencil to
      open the inline edit form, and a red trash button.
    </p>
  </section>

  <section>
    <h2>Create a pool</h2>
    <p>
      Clicking <em>New pool</em> opens an inline card with:
    </p>
    <dl>
      <dt>Name</dt><dd>Required.</dd>
      <dt>Environments</dt><dd>Three icon toggles. A run must be allowed by the pool <em>and</em> by the member device it fans out to.</dd>
      <dt>Description (optional)</dt><dd>Full-width text input.</dd>
      <dt>Devices</dt>
      <dd>
        A dynamic <strong>device picker</strong> (see below). Replaces the old
        UUID-paste textarea — you don't need to copy GUIDs anymore.
      </dd>
    </dl>
    <p>
      <em>Create</em> is disabled until <em>Name</em> has a value. On success
      the card closes, the list refreshes, and a toast confirms.
    </p>
  </section>

  <section>
    <h2>Device picker</h2>
    <p>
      The modern selector for both the create form and the inline edit form.
      Three visual elements stacked vertically:
    </p>

    <h3>1. Header</h3>
    <p>
      A label <em>"Devices · N selected"</em>. When any device is selected, a
      <em>Clear all</em> button appears on the right.
    </p>

    <h3>2. Selected chips</h3>
    <p>
      A rounded pill group showing every picked device as a chip with
      <em>"{'{name}'} · {'{ip}'}"</em> and an × to remove it. The block is
      hidden when nothing is selected.
    </p>

    <h3>3. Search and dropdown</h3>
    <p>
      A search input with a magnifier icon. Focusing it opens a dropdown with
      the first 30 matching devices. The search matches on any of:
      <code>name</code>, <code>ip_address</code>, <code>site</code>,
      <code>role</code>, <code>vendor</code>. Case-insensitive substring match.
    </p>
    <p>
      Each dropdown row shows:
    </p>
    <ul>
      <li>A check-box state on the left (filled primary when selected).</li>
      <li>The device name plus the IP in monospace.</li>
      <li>A metadata line: <em>vendor · platform · site · role</em>.</li>
    </ul>
    <p>
      When the match count exceeds 30 rows, a footer hint says <em>"Showing N of M. Refine the search to see more."</em> — pushing you to narrow rather than scroll a huge list.
    </p>
    <p>
      Empty states: <em>"Loading…"</em>, <em>"No devices yet"</em>,
      <em>"No devices match '{'{query}'}'"</em> respectively.
    </p>
    <Callout tone="success" title="Fast path">
      Type a partial site tag like <code>dc1</code> to filter the dropdown to
      a single rack, then click the header's check-all behaviour by selecting
      rows one at a time — each click toggles that row's membership in the
      pool.
    </Callout>
  </section>

  <section>
    <h2>Edit a pool</h2>
    <p>
      Click the pencil button on any row to reveal the edit section
      underneath. Same fields as the create card, pre-populated from the
      current state:
    </p>
    <ul>
      <li>Name.</li>
      <li>Environment icon toggles.</li>
      <li>Description.</li>
      <li>Device picker, with current members pre-selected as chips.</li>
    </ul>
    <p>
      Adding or removing devices from the picker updates a local buffer — no
      changes hit the backend until you click <em>Save</em>. <em>Cancel</em>
      discards the buffer.
    </p>
  </section>

  <section>
    <h2>Environment checkboxes (inline)</h2>
    <p>
      Three icon toggles on the right side of each pool card — hammer for
      <code>draft</code>, flask for <code>qa</code>, rocket for
      <code>production</code> — flip <code>allow_draft</code> /
      <code>allow_qa</code> / <code>allow_production</code> without opening a
      form. A lit (green) icon means that environment is allowed. Reloads the
      list on success.
    </p>
    <p>
      Pool flags are applied <em>on top of</em> the per-device ones: a run must
      be allowed by the pool <strong>and</strong> by the member it fans out to.
      A pool open to production still only reaches the members that allow
      production. See <a href="/docs/qa-lab">QA lab</a> for the downstream
      effects on workflow promotion.
    </p>
  </section>

  <section>
    <h2>Deleting a pool</h2>
    <p>
      Confirmation dialog. On delete the pool is removed.
    </p>
    <Callout tone="warning" title="Workflows referencing a deleted pool">
      Any workflow that still lists this pool id under <code>target_pools</code>
      will fail to resolve targets until the reference is removed. The pool's
      members (the underlying device rows) are not affected — only the
      grouping.
    </Callout>
  </section>

  <section>
    <h2>Using a pool from a workflow</h2>
    <ol>
      <li>Open a workflow editor at <code>/workflows/{'{id}'}</code>.</li>
      <li>Click <em>Run</em> in the toolbar.</li>
      <li>
        In the <em>Target devices</em> section the picker lets you mix
        individual devices with pools. Pick one or more pools — the run fans
        out over every device each pool resolves at dispatch time.
      </li>
    </ol>
    <p>
      Schedules can also carry pool ids so cron-triggered runs fan out the
      same way.
    </p>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — list pools.</li>
      <li><strong>Operator</strong> — full CRUD.</li>
      <li><strong>Admin</strong> — same as operator.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/devices">Devices</a> — members of pools.</li>
      <li><a href="/docs/qa-lab">QA lab</a> — what the QA flag gates.</li>
      <li><a href="/docs/workflows">Workflows</a> — where pools get used at run time.</li>
    </ul>
  </section>
</DocLayout>
