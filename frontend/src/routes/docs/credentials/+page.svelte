<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Credentials"
  lead="Auth material — username plus password or SSH private key — that devices reference so the engine's SSH/NETCONF handlers can log in at workflow runtime."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/credentials"><code>/credentials</code></a>.
    Sidebar → <strong>Operate</strong> → <strong>Credentials</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A <strong>credential</strong> is a named bundle of auth data stored on
      the server. Plaintext never leaves the backend — the list view returns
      only metadata plus a <code>has_private_key</code> boolean so the UI can
      render a "key uploaded" indicator.
    </p>
    <p>
      Devices point at a credential via <code>credential_id</code> (see
      <a href="/docs/devices">Devices</a>). Handlers like <code>ssh</code>,
      <code>netmiko</code>, and <code>netconf</code> read the referenced
      credential when they need to authenticate against the target.
    </p>
    <Callout tone="info" title="Not the same as AI providers">
      Credentials are for <em>devices</em>. LLM provider keys live under
      <a href="/docs/ai/providers">AI providers</a> with a different backend
      model and encryption domain. They are not interchangeable.
    </Callout>
  </section>

  <section>
    <h2>Auth methods</h2>
    <dl>
      <dt>Password</dt>
      <dd>
        Username + password. Standard for many vendors. Green <em>password</em>
        pill in the list.
      </dd>
      <dt>SSH private key</dt>
      <dd>
        Username + PEM-encoded private key + optional passphrase. The backend
        parses the PEM before storing; malformed keys are rejected up-front so
        you don't find out at 3am that a bad file prevents a run from logging
        in. Green <em>key</em> pill when <code>has_private_key</code> is true,
        red <em>key missing</em> pill otherwise.
      </dd>
    </dl>
  </section>

  <section>
    <h2>List page</h2>
    <p>
      Route: <code>/credentials</code>. Header has <em>Refresh</em> and
      <em>New credential</em> buttons. Large spinner while loading; an empty
      state with a <em>Create first credential</em> button when nothing exists.
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Name</td><td>Display name with a key icon.</td></tr>
        <tr><td>Type</td><td>Handler hint, e.g. <code>ssh</code>, <code>netconf</code>.</td></tr>
        <tr><td>Username</td><td>Optional — some handlers use key-only auth without a user.</td></tr>
        <tr>
          <td>Auth</td>
          <td>
            Pill showing <em>password</em>, <em>key</em>, or <em>key missing</em>.
            The <em>key missing</em> state indicates a credential configured
            for key auth where no key ciphertext is currently stored — usually
            a migration artefact that needs a re-upload.
          </td>
        </tr>
        <tr><td>Updated</td><td>Last modification time.</td></tr>
        <tr><td>Actions</td><td>Edit and Delete.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Create / edit dialog</h2>
    <p>
      The same dialog handles both create and edit. Differences:
    </p>
    <ul>
      <li>Create requires matching secret material for the chosen method (password <em>or</em> private key).</li>
      <li>Edit leaves password and key inputs <strong>blank on open</strong>. Leaving them empty keeps the existing ciphertext; re-entering a value rotates it.</li>
    </ul>

    <h3>Fields</h3>
    <dl>
      <dt>Name</dt><dd>Required. A descriptive label like <code>core-routers-ssh</code>.</dd>
      <dt>Type</dt><dd>Required. Typically <code>ssh</code>. Free-text.</dd>
      <dt>Username</dt><dd>Optional in general; required by some handlers.</dd>
      <dt>Auth method</dt><dd>Select between <em>Password</em> and <em>SSH private key</em>.</dd>
      <dt>Password</dt>
      <dd>
        Masked input. On edit the label reads <em>"New password (leave empty
        to keep current)"</em> so the distinction is explicit.
      </dd>
      <dt>Private key (PEM)</dt>
      <dd>
        Textarea with an 8-row height. Paste the full PEM block (including
        <code>-----BEGIN</code> / <code>-----END</code> lines). The helper
        text under the textarea reminds you the server parses before storing.
        On edit the label shifts to <em>"New private key (leave empty to keep
        current)"</em>.
      </dd>
      <dt>Key passphrase (optional)</dt>
      <dd>
        Only present when auth method is <em>key</em>. Leave empty if the key
        is not passphrase-protected.
      </dd>
    </dl>

    <h3>Validation</h3>
    <ul>
      <li>Name and type are always required.</li>
      <li>On create, the chosen method's secret must be provided.</li>
      <li>
        On edit, switching to <em>key</em> on a row whose
        <code>has_private_key</code> is false forces you to paste a key
        immediately — otherwise the backend would 400.
      </li>
    </ul>
    <p>
      On save, a toast confirms and the list refreshes.
    </p>
  </section>

  <section>
    <h2>Deleting a credential</h2>
    <p>
      Confirmation dialog warns: <em>"Any device referencing it will lose SSH /
      NETCONF access until reassigned."</em>
    </p>
    <Callout tone="warning" title="Blast radius">
      Device rows pointing at a deleted credential keep the stale id. Until
      you re-point them (via the device edit form or bulk-assign), workflows
      that SSH to those devices will fail with a <code>credential not found</code>
      error at handler time.
    </Callout>
  </section>

  <section>
    <h2>Best practices</h2>
    <ul>
      <li>
        Use separate credentials per role (<em>core-ro</em>, <em>core-rw</em>, …)
        so you can rotate without downtime.
      </li>
      <li>
        Prefer key auth for lab and production where possible; store
        passphrases in a password manager, not in workflows.
      </li>
      <li>
        When rotating, update the credential first, then verify by running a
        non-destructive workflow (a ping snippet, for instance) against a
        representative device before widespread usage.
      </li>
    </ul>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — list credentials (metadata only).</li>
      <li><strong>Operator</strong> — full CRUD.</li>
      <li><strong>Admin</strong> — same as operator.</li>
    </ul>
    <p>
      Plaintext values are never returned to any role — not even admins.
    </p>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/devices">Devices</a> — where credentials get assigned.</li>
      <li><a href="/docs/integrations">Integrations</a> — a different credential surface for third-party REST systems.</li>
    </ul>
  </section>
</DocLayout>
