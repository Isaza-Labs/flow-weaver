<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Devices"
  lead="The managed fleet. Each device is a target that workflows can execute against — its IP, platform, vendor, site, role, credential, and the environments it allows all live here."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/devices"><code>/devices</code></a>.
    Sidebar → <strong>Operate</strong> → <strong>Devices</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A <strong>device</strong> is the atomic target a workflow can dispatch
      work against — typically a network element (router, switch, firewall)
      but any SSH- or REST-addressable host works. Devices carry:
    </p>
    <ul>
      <li>Identity (<em>name</em>, <em>IP address</em>).</li>
      <li>Classification (<em>platform</em>, <em>vendor</em>, <em>site</em>, <em>role</em>).</li>
      <li>Authentication (a <em>credential id</em>) — see <a href="/docs/credentials">Credentials</a>.</li>
      <li>The environments it allows (<em>draft</em>, <em>qa</em>, <em>production</em>, shown as three icon toggles) — a run only fans out to devices that allow the environment of the workflow being run.</li>
    </ul>
  </section>

  <section>
    <h2>The page at a glance</h2>
    <p>
      One full-page table preceded by an optional create form and warning
      banner. The header has two contextual actions:
    </p>
    <ul>
      <li>
        <strong>Assign credential (N)</strong> — only visible when at least one
        row is selected. Opens the bulk-assign dialog.
      </li>
      <li><strong>New device</strong> — toggles the create form below.</li>
    </ul>
  </section>

  <section>
    <h2>Missing-credential warning</h2>
    <p>
      A yellow banner appears above the table whenever any device has no
      credential (<code>credential_id</code> is null or the all-zeros GUID).
      It counts the affected rows and warns that SSH / NETCONF workflows will
      fail with <code>credential 00000000-… not found</code>. The banner links
      to <a href="/credentials"><code>/credentials</code></a> so you can fix
      the root cause.
    </p>
  </section>

  <section>
    <h2>Create form</h2>
    <p>
      Expands under the header when you click <em>New device</em>. Three-column
      grid on wide screens, stacked on mobile.
    </p>
    <dl>
      <dt>Name</dt><dd>Required. Typically the hostname.</dd>
      <dt>IP address</dt><dd>Required. IPv4 or IPv6.</dd>
      <dt>Platform</dt><dd>Netmiko platform identifier (<code>cisco_ios</code>, <code>arista_eos</code>, <code>juniper_junos</code>, etc.).</dd>
      <dt>Vendor</dt><dd>Free-text display vendor (<code>Cisco</code>, <code>Arista</code>, …).</dd>
      <dt>Site</dt><dd>Free-text site/location.</dd>
      <dt>Role</dt><dd>Free-text role (<code>core</code>, <code>edge</code>, <code>spine</code>, <code>leaf</code>, …).</dd>
      <dt>Credential</dt>
      <dd>
        Select from the stored credentials. Options come from
        <a href="/credentials"><code>/credentials</code></a>; if no credentials
        exist, the only option is <em>(none)</em>. Empty selection maps to the
        all-zeros GUID on the wire.
      </dd>
    </dl>
    <p>
      The <em>Create device</em> button is disabled until both <em>Name</em>
      and <em>IP address</em> have values. On success the form resets, closes,
      and the list refreshes.
    </p>
  </section>

  <section>
    <h2>SSH host key fingerprint</h2>
    <p>
      Edit-dialog field only — at create time nobody has connected yet, so
      there is no key to paste. It pins the device's SSH host key: once set,
      an <code>ssh</code> step <strong>refuses to connect</strong> if the
      device presents a different key. That is what stops someone on the
      network path impersonating the device and collecting the credential the
      step was about to send.
    </p>
    <p>
      You rarely need to fill it in by hand. A device with no pin gets one
      automatically from its <strong>first successful connect</strong>
      (trust-on-first-use), so the window where any key is accepted is that
      one run. To set it deliberately, paste either the
      <code>ssh-keygen -lf</code> output or the
      <code>host_key_fingerprint</code> an <code>ssh</code> step reports in its
      output — with or without the <code>SHA256:</code> prefix.
    </p>
    <p>
      After a legitimate key rotation the step will start failing with a host
      key mismatch. That is the control working: clear the field, and the next
      connect re-pins the new key. MD5 colon-hex fingerprints (from older
      <code>ssh</code> clients) and shortened digests are rejected — a
      truncated pin would match keys that differ.
    </p>
  </section>

  <section>
    <h2>Search and counts</h2>
    <p>
      Above the table: a search input (<em>"Filter by name, IP, site or role…"</em>).
      Matching is client-side, case-insensitive, substring. When a filter is
      active, a small counter reads <em>"N / M matches"</em>.
    </p>
    <p>
      The full list is loaded once; the filter is in memory. No round-trip per
      keystroke.
    </p>
  </section>

  <section>
    <h2>Columns</h2>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>(checkbox)</td><td>Row selection. Header checkbox selects every visible row (filter-scoped), with an indeterminate state when only some are checked.</td></tr>
        <tr><td>Name</td><td>Display name.</td></tr>
        <tr><td>IP</td><td>IP address, monospace.</td></tr>
        <tr><td>Platform</td><td>Driver platform (monospace).</td></tr>
        <tr><td>Vendor</td><td>Free-text vendor.</td></tr>
        <tr><td>Site</td><td>Free-text site.</td></tr>
        <tr><td>Role</td><td>Free-text role.</td></tr>
        <tr>
          <td>Credential</td>
          <td>
            When a credential is assigned, shows its name with a small key icon.
            When missing, shows a yellow <em>none</em> warning pill.
          </td>
        </tr>
        <tr>
          <td>Environments</td>
          <td>
            Three icon toggles — hammer <em>draft</em>, flask <em>qa</em>,
            rocket <em>production</em>. Lit means allowed. Each click flips that
            flag in place. With all three off the row is marked <em>parked</em>.
          </td>
        </tr>
        <tr><td>Actions</td><td>Edit (pencil) and Delete (trash) icon buttons.</td></tr>
      </tbody>
    </table>

    <h3>Empty states</h3>
    <ul>
      <li><strong>No devices at all</strong> — big empty state with a <em>Server</em> icon and an <em>Add device</em> button.</li>
      <li><strong>Filter matches nothing</strong> — empty state with a <em>Clear filter</em> button that empties the search box.</li>
    </ul>
  </section>

  <section>
    <h2>Select all (filter-scoped)</h2>
    <Callout tone="info">
      The header checkbox selects every row <em>currently visible</em>. With a
      search filter active, selecting 3 out of 500 means "these 3", not
      "every row".
    </Callout>
    <p>
      A <em>N selected</em> suffix appears in the footer line and the
      <em>Assign credential</em> button lights up in the header.
    </p>
  </section>

  <section>
    <h2>Edit dialog</h2>
    <p>
      Opened from the pencil icon. Same field set as the create form, inside a
      large modal. Disabled inputs while saving. A yellow warning inside the
      dialog reminds you when the device has no credential yet.
    </p>
    <p>
      <em>Save changes</em> is disabled until <em>Name</em> has a value. On
      success a toast confirms; on failure an alert at the top of the dialog
      holds the error so the form is not reset.
    </p>
  </section>

  <section>
    <h2>Environments column</h2>
    <p>
      Three independent icon toggles per row — a hammer for <code>draft</code>,
      a flask for <code>qa</code>, a rocket for <code>production</code> — each
      flipping <code>allow_draft</code>, <code>allow_qa</code> or
      <code>allow_production</code> with a single PUT. A lit (green) icon means
      that environment is allowed; a muted one means it is not. No dialog. A run
      resolves its targets against the environment of the workflow being run and
      drops every device that doesn't allow it. This applies to every
      environment, <code>draft</code> included. A new device allows
      <code>draft</code> and <code>production</code> but not <code>qa</code>.
    </p>
    <p>
      Any combination is valid. Ticking only <code>production</code> gives you
      a device no draft or qa run can ever touch; unticking all three parks it
      — no run can target it at all, and the row says so. When a run's whole
      target selection is dropped this way it is refused outright rather than
      executing against nothing. See <a href="/docs/qa-lab">QA lab</a> for how
      this interacts with workflow promotion.
    </p>
  </section>

  <section>
    <h2>Bulk-assign credential dialog</h2>
    <p>
      Opened from the header's <em>Assign credential (N)</em> button when any
      rows are selected. A single select picks the target credential; an
      explicit <em>(none — unset credential)</em> option clears whatever each
      device currently has.
    </p>
    <Callout tone="info" title="Best-effort batching">
      The dialog fires one <code>PUT /api/device/{'{id}'}</code> per selected
      row. Failures don't abort the batch — at the end a toast reports
      <em>"Assigned N/M devices — K failed"</em> so you can retry the outliers.
      Useful after a bulk inventory sync drops dozens of devices with no
      credential attached.
    </Callout>
    <p>
      On completion the selection is cleared and the list refreshes.
    </p>
  </section>

  <section>
    <h2>Deleting a device</h2>
    <p>
      The trash icon asks for confirmation. On delete the row disappears from
      the table and from any selection set it was part of. Any device pool
      that statically referenced this device will keep the GUID in its list;
      the pool resolution treats missing members as empty.
    </p>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — list and browse devices.</li>
      <li><strong>Operator</strong> — full CRUD, environment toggles, bulk assign.</li>
      <li><strong>Admin</strong> — same as operator.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/credentials">Credentials</a> — the auth material devices reference.</li>
      <li><a href="/docs/device-pools">Device pools</a> — group devices for workflow targeting.</li>
      <li><a href="/docs/qa-lab">QA lab</a> — what the QA flag controls.</li>
    </ul>
  </section>
</DocLayout>
