<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Audit log"
  lead="Two-tab view combining domain writes (workflow, device, credential, etc.) with auth events (login success, failure, lockout). Filter by entity, action, user, or date; expand any row for details."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/audit"><code>/admin/audit</code></a>.
  </Callout>
  <Callout tone="admin" title="Admin only">
    Requires the <code>admin</code> role.
  </Callout>

  <section>
    <h2>Two event streams, one page</h2>
    <p>
      FlowWeaver emits audit rows from two different sources:
    </p>
    <dl>
      <dt>Domain events</dt>
      <dd>
        Every meaningful write on your data — a workflow created, a device
        updated, a credential rotated, a policy toggled. One row per write.
      </dd>
      <dt>Auth events</dt>
      <dd>
        Authentication lifecycle — <code>login_success</code>,
        <code>login_failure</code>, <code>logout</code>,
        <code>password_change</code>, <code>lockout</code>,
        <code>refresh</code>, <code>token_revoked</code>.
      </dd>
    </dl>
    <p>
      Both are exposed through tabs at the top of the page. Pagination is
      50 rows per page, offset-based.
    </p>
  </section>

  <section>
    <h2>Filters (shared)</h2>
    <p>
      Above the table, a row of filters applies to both tabs:
    </p>
    <dl>
      <dt>Entity type</dt>
      <dd>Text input — free-form match against the domain event's entity. Only used on the Domain tab.</dd>
      <dt>Entity id</dt>
      <dd>
        The whole history of a single record — every change to one workflow,
        credential or device. Paste the id from a row you are already looking
        at. Only used on the Domain tab.
      </dd>
      <dt>Action</dt>
      <dd>Text input. Examples: <code>create</code>, <code>update</code>, <code>delete</code>, <code>promote</code>. Only used on the Domain tab.</dd>
      <dt>Actor</dt>
      <dd>
        Who made the change, by name. A username for a signed-in person, or
        the automation identity for unattended changes — <code>workflow-runner</code>,
        <code>git-webhook</code>, <code>workflow-webhook</code>,
        <code>messaging-send</code>. Automation has no user id, so this is the
        only way to filter it. Only used on the Domain tab.
      </dd>
      <dt>Auth event kind</dt>
      <dd>Select, bounded to the known kinds. Only used on the Auth tab.</dd>
      <dt>User</dt>
      <dd>Filter by user id. The select lists the accounts from the Users screen.</dd>
      <dt>From / to</dt>
      <dd>Date inputs (<code>YYYY-MM-DD</code>, local). Widened to UTC day boundaries on the wire.</dd>
    </dl>
    <p>
      Any filter change resets the pagination to page 0.
    </p>
  </section>

  <section>
    <h2>Domain events tab</h2>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Timestamp</td><td>When the event was written. Expandable row reveals the full ISO value.</td></tr>
        <tr><td>Actor</td><td>Username (from the users map), or — when nobody was signed in — the automation identity that made the change, tagged <em>automation</em> under the name.</td></tr>
        <tr><td>Action</td><td>What happened (<code>create</code>, <code>update</code>, …).</td></tr>
        <tr><td>Entity</td><td>The entity type plus the entity id. Truncated in the cell; full in the expander.</td></tr>
        <tr><td>Before / After</td><td>Shown inside the expander — two JSON panes showing the diff for update events.</td></tr>
        <tr><td>Request id</td><td>Correlation id to pivot to traces and the backend logs. It is the same <code>X-Request-Id</code> value those two carry, so the three sources join on it.</td></tr>
      </tbody>
    </table>

    <h3>Row expander</h3>
    <p>
      The chevron on each row opens a detail panel with the full JSON
      payload, the full timestamp, and copy-to-clipboard on the request id.
    </p>
  </section>

  <section>
    <h2>Auth events tab</h2>
    <p>
      Same shape but narrower. Columns: timestamp, actor, event kind
      (colored badge), IP address, request id. Failures and lockouts are
      highlighted in red; successes in green; neutral kinds
      (<code>logout</code>, <code>refresh</code>) in grey.
    </p>
    <p>
      Useful for investigating single-user lockouts — filter by user, then
      look for the sequence <code>login_failure</code> &times; 5 →
      <code>lockout</code>.
    </p>
    <h3>Unattributed failures</h3>
    <p>
      A sign-in attempt that never resolves to a user — the username simply
      does not exist — is recorded with no actor attached. These are the
      username-enumeration attempts, and they are hidden by default so they
      do not drown out failures on real accounts. Pass
      <code>include_unattributed=true</code> to
      <code>/api/auth/events</code> to see them; the metadata carries the
      username the caller <em>tried</em>, and nothing else.
    </p>
    <p>
      The admin dashboard's <em>Sign-in activity</em> chart does not separate
      them: they count as <code>login_failure</code>, so a spike there can come
      from enumeration attempts as well as real accounts.
    </p>
  </section>

  <section>
    <h2>CSV export</h2>
    <p>
      The <em>Download</em> button in the header exports the <strong>currently
      filtered</strong> rows as CSV. Useful for compliance bundles and
      ticket attachments.
    </p>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/admin/traces">Traces</a> — finer-grained action trail that complements the audit log with <em>every</em> handler call (including reads).</li>
      <li><a href="/docs/admin">Admin dashboard</a> — its <em>Sign-in activity</em> chart summarizes the auth stream.</li>
      <li><a href="/docs/policies">Policies</a> — changes here leave audit rows too.</li>
    </ul>
  </section>
</DocLayout>
