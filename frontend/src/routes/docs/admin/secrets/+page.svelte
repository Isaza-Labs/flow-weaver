<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Secrets"
  lead="Named, encrypted values (API keys, bearer tokens, passwords) that API specs and some workflow steps reference by name, so the plaintext never appears in a spec, a node, or the UI."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/secrets"><code>/admin/secrets</code></a>
    (breadcrumb <strong>Admin</strong> → <strong>Secrets</strong>). The page is
    not in the sidebar. Open it from the <strong>AI</strong> hub
    (<a href="/ai"><code>/ai</code></a>, sidebar → <strong>Intelligence</strong>
    → <strong>AI</strong>) with the <strong>Secrets (admin)</strong> link, or type
    the URL.
  </Callout>
  <Callout tone="admin" title="Admin only">
    Every <code>/api/secrets</code> endpoint requires the <code>admin</code> role.
    In the browser, any <code>/admin/…</code> route sends a non-admin back to the
    dashboard.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      A secret stores a credential once, encrypted, under a stable name. Anything
      that needs the credential holds a <em>reference</em> such as
      <code>{'${secret:secret:netbox-token:value}'}</code>, never the value. The
      backend replaces the reference with the plaintext at the moment of the call.
      Rotating the value takes effect everywhere the name is used, and nothing has
      to be edited.
    </p>
    <p>
      The page describes secrets as <em>"Named credentials the AI agent consumes
      when calling REST operations."</em> That is their main use: the
      <code>x-credential-ref</code> of an <a href="/docs/ai/specs">API spec</a>. The
      same references also work in a few workflow step types. See
      <a href="#where-resolved">Where references are resolved</a>.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>You need the <code>admin</code> role.</li>
      <li>
        Have the credential ready to paste. After you save it, nobody can read it
        back, not even an admin.
      </li>
      <li>
        Choose a name that follows the rule below. Once the secret is created you
        cannot rename it.
      </li>
      <li>
        Device logins do <strong>not</strong> belong here. Store them as
        <a href="/docs/credentials">Credentials</a> (<code>/credentials</code>).
        A workflow can still reference those with the <code>credential</code>
        source described below.
      </li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>

    <h3>What a secret row holds</h3>
    <p>
      There is only one kind of secret. It has no type or category field. Each row
      has:
    </p>
    <dl>
      <dt>Name</dt>
      <dd>
        The lookup key. It must match
        <code>^[a-z0-9](?:[a-z0-9_-]{'{1,62}'}[a-z0-9])?$</code>: lowercase
        letters, digits, hyphens and underscores, starting and ending with a
        letter or digit, up to 64 characters. Read the pattern literally, because
        it is stricter than the error message's "2–64 chars" suggests: a
        <strong>single</strong> character is accepted (<code>x</code>), then
        nothing until <strong>three</strong>. A two-character name can never
        match — the optional second half needs at least one interior character
        plus the closing letter or digit — so <code>db</code> is refused while
        <code>dbx</code> and <code>db1</code> are fine. Names are unique and
        <strong>immutable</strong>.
      </dd>
      <dt>Description</dt>
      <dd>Optional free text. It appears only on this page.</dd>
      <dt>Value</dt>
      <dd>
        Encrypted at rest with the same key ring as device credentials. The API
        only reports whether a value is present (<code>has_value</code>). No
        endpoint reveals the value.
      </dd>
      <dt>Created by / timestamps</dt>
      <dd>Recorded for auditing. The table shows <em>Updated</em>.</dd>
    </dl>

    <h3>Reference syntax</h3>
    <p>
      Every reference has the form
      <code>{'${secret:<source>:<id-or-name>:<field>}'}</code>. The
      <code>&lt;id-or-name&gt;</code> part is read as an id when it parses as a
      GUID, and as a name otherwise. Five sources are supported:
    </p>
    <table>
      <thead><tr><th>Reference</th><th>Resolves to</th></tr></thead>
      <tbody>
        <tr>
          <td><code>{'${secret:secret:<name>:value}'}</code></td>
          <td>A secret from this page. The field must be <code>value</code>. This is the string the <strong>Ref</strong> button copies.</td>
        </tr>
        <tr>
          <td><code>{'${secret:credential:<id|name>:<field>}'}</code></td>
          <td>A <a href="/docs/credentials">device credential</a>. Valid fields are <code>username</code>, <code>password</code>, and <code>private_key</code> (also accepted as <code>privatekey</code>).</td>
        </tr>
        <tr>
          <td><code>{'${secret:ai_provider:<id|name>:api_key}'}</code></td>
          <td>The API key of an <a href="/docs/ai/providers">AI provider</a>. <code>apikey</code> is also accepted.</td>
        </tr>
        <tr>
          <td><code>{'${secret:integration:<id|name>:<path>}'}</code></td>
          <td>A value from an <a href="/docs/integrations">integration</a>'s stored auth configuration. The path is dotted, for example <code>token</code> or <code>basic.password</code>.</td>
        </tr>
        <tr>
          <td><code>{'${secret:session:current:jwt}'}</code></td>
          <td>The access token of the user who invoked the agent. Specs that call FlowWeaver's own API use it so that the call runs with that user's permissions.</td>
        </tr>
      </tbody>
    </table>
    <Callout tone="warning" title="Unresolved references stay literal">
      A reference that can't be resolved (unknown name, deleted secret, wrong
      field) is <strong>not</strong> replaced with an empty string. The marker text
      itself is sent instead. You usually see this as an authentication failure
      from the remote system (for example HTTP 401), with the original marker
      visible in the request.
    </Callout>

    <h3 id="where-resolved">Where references are resolved</h3>
    <table>
      <thead><tr><th>Place</th><th>What is resolved</th></tr></thead>
      <tbody>
        <tr>
          <td><a href="/docs/ai/specs">API spec</a> used by the agent's <code>execute_operation</code> tool</td>
          <td>The <code>x-credential-ref</code> of each security scheme (bearer, basic, apiKey in a header or query), the server URL, and the JSON request body.</td>
        </tr>
        <tr>
          <td><code>python_snippet</code> step</td>
          <td>Every string value, at any depth, in the step's input (the node's <code>config_overrides</code>). Resolution happens before the script starts.</td>
        </tr>
        <tr>
          <td><code>ssh</code> step</td>
          <td>The <code>username</code>, <code>password</code>, <code>private_key</code>, <code>key_passphrase</code> and <code>enable_secret</code> inputs.</td>
        </tr>
        <tr>
          <td><code>rest_call</code> step</td>
          <td>The <code>url</code> and each <code>headers</code> value. The <code>body</code> is <strong>not</strong> resolved.</td>
        </tr>
        <tr>
          <td>Any other step type</td>
          <td>Nothing. The marker arrives as literal text.</td>
        </tr>
      </tbody>
    </table>
    <p>
      <code>integration_action</code> nodes don't use references. They
      authenticate with the auth stored on the <a href="/docs/integrations">integration</a>.
    </p>

    <h3>When the value is read</h3>
    <p>
      Lookups hit the database every time and are never cached. Resolution happens
      when each call or step executes, not when a run is queued or a schedule is
      created. Consequences:
    </p>
    <ul>
      <li>After a <strong>rotation</strong>, the next step or call that resolves the reference uses the new value. This includes steps of runs that are already in progress and scheduled runs that fire later.</li>
      <li>After a <strong>delete</strong>, every later resolution fails and the marker stays literal (see above). The confirmation dialog warns: <em>"Any spec referencing it will break until a replacement is created."</em></li>
    </ul>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Header</h3>
    <p>
      The title is <strong>Secrets</strong>, with two buttons: <strong>Refresh</strong>
      and <strong>New secret</strong>. Below the header, an info banner says that
      plaintext values are only readable by the backend and that you rotate a
      secret by editing it and pasting a new value.
    </p>

    <h3>Table</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Name</td><td>The secret name, in monospace.</td></tr>
        <tr><td>Description</td><td>The description, or <code>—</code> when empty.</td></tr>
        <tr><td>Value</td><td>A <code>set</code> badge when ciphertext is stored, <code>empty</code> otherwise.</td></tr>
        <tr><td>Updated</td><td>When the row last changed.</td></tr>
        <tr>
          <td>Actions</td>
          <td>
            <strong>Ref</strong> copies <code>{'${secret:secret:<name>:value}'}</code>
            to the clipboard and confirms with a <em>"Reference copied"</em> toast.
            If the copy fails, the toast shows the reference so you can copy it by
            hand. <strong>Edit</strong> opens the dialog. <strong>Delete</strong>
            removes the secret after a confirmation.
          </td>
        </tr>
      </tbody>
    </table>
    <p>Rows are sorted by name. Deleted secrets are not listed.</p>

    <h3>New secret / Edit secret dialog</h3>
    <dl>
      <dt>Name</dt>
      <dd>
        Placeholder <code>netbox-token</code>. Checked in the browser against the
        same rule as the backend. The field is disabled when editing, with the
        note <em>"Names are immutable — references in specs stay stable."</em>
      </dd>
      <dt>Description</dt>
      <dd>Optional. Two lines.</dd>
      <dt>Value (create) / New value (leave empty to keep current) (edit)</dt>
      <dd>
        Required on create. On edit, leave it blank to change only the
        description. Anything you type <strong>rotates</strong> the stored value.
      </dd>
    </dl>
    <p>
      The footer has <strong>Cancel</strong> and <strong>Create</strong> (or
      <strong>Save changes</strong> when editing). Validation and server errors
      appear in a red alert at the top of the dialog.
    </p>
  </section>

  <section>
    <h2>Main procedure</h2>

    <h3>Create a secret</h3>
    <ol>
      <li>Click <strong>New secret</strong>.</li>
      <li>Enter a <strong>Name</strong>, and optionally a <strong>Description</strong>.</li>
      <li>Paste the credential into <strong>Value</strong>.</li>
      <li>Click <strong>Create</strong>. A <em>"Secret created"</em> toast confirms it, and the row appears with a <code>set</code> badge.</li>
      <li>Click <strong>Ref</strong> on the row and paste the reference wherever the secret is needed.</li>
    </ol>

    <h3>Edit the description</h3>
    <ol>
      <li>Click <strong>Edit</strong> on the row.</li>
      <li>Change <strong>Description</strong>, and leave <strong>New value</strong> empty.</li>
      <li>Click <strong>Save changes</strong>. The stored value is untouched.</li>
    </ol>

    <h3>Rotate a value</h3>
    <ol>
      <li>Click <strong>Edit</strong> and paste the new credential into <strong>New value</strong>.</li>
      <li>
        Click <strong>Save changes</strong>. A confirmation titled
        <em>Rotate secret "&lt;name&gt;"?</em> warns: <em>"Rotating replaces the
        credential immediately; in-flight specs will use the new value."</em>
      </li>
      <li>Click <strong>Rotate</strong>. The old value is gone, and FlowWeaver keeps no history of previous values.</li>
    </ol>
    <Callout tone="success" title="Rotate safely">
      Create the new key at the provider first, rotate here, confirm that a call
      works, and only then revoke the old key at the provider. FlowWeaver switches
      to the new value immediately, so revoking first opens a window in which calls
      fail.
    </Callout>

    <h3>Delete a secret</h3>
    <ol>
      <li>Click <strong>Delete</strong> on the row.</li>
      <li>Confirm the dialog <em>Delete secret "&lt;name&gt;"?</em> with <strong>Delete</strong>.</li>
    </ol>
    <p>
      This is a soft delete. The row is hidden and stops resolving, but it stays in
      the database so that audit entries still point to it. There is no undelete in
      the UI. See <a href="#errors">Errors and recovery</a> for the effect on
      re-creating the same name.
    </p>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Goal:</strong> call a NetBox API with a token, both from the agent and
      from a workflow, without the token appearing anywhere.
    </p>
    <p>
      <strong>Prerequisites:</strong> the <code>admin</code> role and a NetBox API
      token. The values below are placeholders. Replace
      <code>&lt;YOUR-NETBOX-TOKEN&gt;</code> and <code>netbox.example.com</code>
      with your own.
    </p>
    <ol>
      <li>
        On <code>/admin/secrets</code>, create a secret named
        <code>netbox-token</code> with the value
        <code>&lt;YOUR-NETBOX-TOKEN&gt;</code>.
      </li>
      <li>
        <strong>For the agent:</strong> on <a href="/ai/specs"><code>/ai/specs</code></a>,
        reference it from the spec's security scheme:
        <pre><code>{`components:
  securitySchemes:
    token:
      type: http
      scheme: bearer
      x-credential-ref: \${secret:secret:netbox-token:value}
security:
  - token: []`}</code></pre>
      </li>
      <li>
        <strong>For a workflow:</strong> in a <code>rest_call</code> node, put the
        reference in a header value. The reference can sit inside a longer string:
        <pre><code>{`{
  "url": "https://netbox.example.com/api/dcim/devices/",
  "method": "GET",
  "headers": {
    "Authorization": "Token \${secret:secret:netbox-token:value}"
  }
}`}</code></pre>
      </li>
    </ol>
    <p>
      <strong>Expected result:</strong> the agent's operation and the workflow step
      both authenticate. The step log records the URL as written and does not
      include headers. A <code>secret.access</code> trace event with status
      <code>completed</code> is recorded for each resolution.
    </p>
    <p>
      <strong>Common error:</strong> the call returns HTTP 401 because the
      reference contains a typo (for example <code>netbox_token</code> instead of
      <code>netbox-token</code>), or the field isn't <code>value</code>. The marker
      is sent literally.
      <strong>Recovery:</strong> on <a href="/admin/traces"><code>/admin/traces</code></a>,
      look for a <code>secret.access</code> event with status <code>failed</code>
      and error <code>unresolved</code>. Its metadata shows the source, name and
      field that were requested. Fix the reference, or click <strong>Ref</strong>
      to copy the exact string, and run again.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li>
        <strong>Managing</strong> secrets (list, create, edit, rotate, delete)
        requires the <code>admin</code> role itself. No permission grant can give
        this access to anyone else.
      </li>
      <li>
        <strong>No reveal.</strong> Responses carry only metadata and
        <code>has_value</code>. The value is not in the audit payload either.
      </li>
      <li>
        <strong>Audit trail.</strong> Create, update and delete write an audit
        entry (entity <code>secret</code>). An update records
        <code>value_rotated</code> but never the value. Matching
        <code>secret.create</code> / <code>secret.update</code> /
        <code>secret.delete</code> events appear in traces. See
        <a href="/docs/admin/audit">Audit</a>.
      </li>
      <li>
        <strong>Access trail.</strong> Every resolution (from any source) records a
        <code>secret.access</code> trace event in the <code>security</code>
        category. It holds the source, the id or name, the field, and the value
        length (<code>value_bytes</code>), never the value. A burst of
        <code>failed</code> events points to a deleted secret or to someone probing
        names. See <a href="/docs/admin/traces">Traces</a>.
      </li>
    </ul>
    <Callout tone="warning" title="Who can make a step use a secret">
      <p>
        Writing a reference into a node's config is the same as reading the secret —
        a <code>python_snippet</code> receives the plaintext and can print it. So
        saving a node whose config contains <code>${'{'}secret:…{'}'}</code> requires
        <code>secret.read</code> (admin, unless granted in granular mode); the
        editor, the import wizard and the assistant refuse it otherwise, naming the
        node.
      </p>
      <p>
        The gate only looks at what a save <strong>adds</strong>. Each reference
        already stored is fingerprinted as the triple
        <em>(the node's snippet, the field path inside
        <code>config_overrides</code>, the reference text itself)</em>, and the
        incoming node is compared against the same node in the saved graph. If
        every fingerprint on it was already there, the save goes through without
        the check — so someone without <code>secret.read</code> can still rename
        a workflow, move a node or reformat a payload that happens to contain a
        reference. What counts as new is anything that changes that triple:
        adding a reference, moving a saved one to another field or another node,
        or pointing the node at a different snippet. Each of those is a fresh
        reference and is gated.
      </p>
      <p>
        References only work where an authorised author wrote them. In run input
        (manual input, webhook bodies, trigger <code>input_defaults</code>), in
        upstream step outputs and in device or run fields they are neutralized
        before any step sees them and reach the step as plain text. The worker
        still resolves the references already saved in a workflow for whoever runs
        it, so review who may run workflows that use secrets.
      </p>
      <p>
        A <strong>subflow</strong> child is the exception to that neutralizing,
        and deliberately so. Its input is the parent step's payload, which was
        already built from a neutralized run input plus the parent workflow's own
        authored config — so the only references that can be in it are the
        parent's author's, and they stay live. The child resolves them normally
        instead of receiving them as plain text.
      </p>
    </Callout>

    <h3>Keep secrets out of code and logs</h3>
    <ul>
      <li>Never paste a credential into snippet code, a node's configuration, or a spec. Use a reference.</li>
      <li>
        A <code>python_snippet</code> receives the <strong>plaintext</strong>.
        Anything the script prints or returns with <code>set_output()</code> is
        stored with the run. Never echo a secret or include it in the output.
      </li>
      <li>
        Put references in the fields that are resolved. In a
        <code>rest_call</code>, a reference in the <code>body</code> is sent
        literally. Use a header instead.
      </li>
      <li>
        No general log scrubber runs on step output. FlowWeaver redacts
        recognisable secret shapes (provider API keys, JWTs,
        <code>Authorization</code> headers, <code>password=</code> pairs, long hex
        strings) only in integration test results and in agent prompts stored with
        reports. Don't rely on redaction to protect a secret.
      </li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <dl>
      <dt>Loading</dt><dd>A large spinner replaces the table.</dd>
      <dt>Load error</dt><dd>A red alert with the error message, plus a <em>"Couldn't load secrets"</em> toast. Click <strong>Refresh</strong> to try again.</dd>
      <dt>No secrets</dt>
      <dd>
        <em>"No secrets yet"</em>, with the hint <em>"Add credentials the agent
        needs (API keys, bearer tokens, basic-auth passwords). Specs reference them
        by name."</em> and a <strong>Create first secret</strong> button.
      </dd>
    </dl>
  </section>

  <section id="errors">
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>Message</th><th>Cause</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr>
          <td><em>Name must be 2–64 chars, lowercase letters/digits/hyphens/underscores.</em></td>
          <td>The name breaks the naming rule. Checked in the browser and again on the server (HTTP 400, code <code>secret_invalid</code>). The message's "2–64" is not what the pattern enforces — a two-character name is rejected, a one-character name accepted.</td>
          <td>Use lowercase only, start and end with a letter or digit, and avoid exactly two characters: make it one character or three or more.</td>
        </tr>
        <tr>
          <td><em>Value is required when creating a secret.</em> / <em>value is required</em></td>
          <td>The <strong>Value</strong> field was empty on create.</td>
          <td>Paste the value.</td>
        </tr>
        <tr>
          <td><em>a secret with that name already exists</em></td>
          <td>HTTP 409, code <code>secret_name_taken</code>. The name is in use, <strong>including by a deleted secret</strong>. See the limitation below.</td>
          <td>Edit the existing secret (rotate it) instead, or choose a new name and update the references.</td>
        </tr>
        <tr>
          <td><em>value cannot be empty — delete the secret to remove it</em></td>
          <td>HTTP 400, code <code>secret_value_empty</code>. The API received an empty value on update. The UI never sends one, because a blank field means "keep current".</td>
          <td>Send a non-empty value, or delete the secret.</td>
        </tr>
        <tr>
          <td><em>We couldn't find what you're looking for…</em></td>
          <td>HTTP 404. The secret was deleted in another tab.</td>
          <td>Click <strong>Refresh</strong>.</td>
        </tr>
        <tr>
          <td><em>Too many requests…</em></td>
          <td>HTTP 429. Rate limit on write operations.</td>
          <td>Wait a few seconds and retry.</td>
        </tr>
        <tr>
          <td>Remote API answers 401 / SSH authentication fails</td>
          <td>The reference didn't resolve and was sent literally.</td>
          <td>Look for a failed <code>secret.access</code> trace, fix the reference or re-create the secret, then run again.</td>
        </tr>
      </tbody>
    </table>
    <p>
      <strong>Reverting a mistake:</strong> an accidental rotation can't be undone,
      because FlowWeaver keeps no history. Rotate again with the correct value. An
      accidental delete also can't be undone, and the name stays blocked. Create a
      secret with a new name and update every reference.
    </p>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>
        <strong>Deleted names can't be reused.</strong> Deletion is soft, and the
        name check and unique index both include deleted rows, so re-creating a
        deleted name returns <code>secret_name_taken</code>.
      </li>
      <li>Names can't be changed, and values can't be viewed or exported.</li>
      <li>There is no version history or scheduled rotation, and no expiry date.</li>
      <li>There is no "where used" view. Search your specs and workflows for the name before you delete a secret.</li>
      <li>
        References are resolved only in the places listed above. Other step types,
        including <code>ansible_playbook</code>, <code>email_send</code> and
        <code>slack_message</code>, receive the marker as literal text.
      </li>
      <li>
        A workflow bundle never carries secret values. Importing one keeps every
        <code>${'{'}secret:…{'}'}</code> reference verbatim, wherever it sits, and
        adds a note per reference listing where it was used and that the secret
        must exist here before the first run. Create the secrets under the same
        names on the destination instance.
      </li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai/specs">API specs</a>, where <code>x-credential-ref</code> is declared.</li>
      <li><a href="/docs/credentials">Credentials</a>, for device logins (the <code>credential</code> source).</li>
      <li><a href="/docs/integrations">Integrations</a>, which store their own auth (the <code>integration</code> source).</li>
      <li><a href="/docs/snippets">Snippets</a>, covering <code>python_snippet</code>, <code>ssh</code> and <code>rest_call</code>.</li>
      <li><a href="/docs/admin/traces">Traces</a>, for <code>secret.access</code> events.</li>
      <li><a href="/docs/admin/audit">Audit</a>, for the create, update and delete history.</li>
    </ul>
  </section>
</DocLayout>
