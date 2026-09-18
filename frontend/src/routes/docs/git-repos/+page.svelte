<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Git repositories"
  lead="Register Git remotes that FlowWeaver clones into a server-side working copy. You can browse and edit files there, commit and push from a workflow's git step, and start workflows from push webhooks."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Build</strong> → <strong>Git repos</strong>
    (<a href="/integrations/git"><code>/integrations/git</code></a>). Click a repository name
    to open its detail page at <code>/integrations/git/&lt;id&gt;</code>.
  </Callout>
  <Callout tone="admin" title="Admin for anything that writes">
    Anyone with <code>git.read</code> (every built-in role) can list repositories and browse
    files. Registering, editing, deleting, pulling, pushing, switching branch and committing
    need <code>git.manage</code>, which only the <code>admin</code> role has by default.
    Creating, editing and deleting webhooks needs <code>gitwebhook.manage</code>, also
    admin-only by default.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      A <strong>Git repository</strong> registration is a remote URL, a default branch and,
      optionally, a stored credential. FlowWeaver clones the remote on first use into a
      working copy on the server. Everything else acts on that copy: the file browser,
      the commit dialog, the <code>git</code> workflow step and the AI agent's git tools.
    </p>
    <p>
      Typical uses: keep device configs in Git and have a workflow commit what it generated,
      read a template from a repo during a run, or start a workflow whenever someone pushes
      to a branch.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>
        <strong>An HTTPS remote</strong> needs a credential in
        <a href="/docs/credentials">Credentials</a> whose <strong>Type</strong> contains
        <code>git</code> (for example <code>git_token</code>). Put the account name in
        <em>Username</em> and the personal access token in <em>Password</em>. An empty username
        is sent as <code>git</code>. The token needs read access, and write access if you will
        push.
      </li>
      <li>
        <strong>An SSH remote</strong> (<code>git@host:org/repo.git</code> or
        <code>ssh://…</code>) needs a credential with <strong>Auth method = SSH private
        key</strong>. The form suggests <em>Type=ssh</em>. The public key must be registered
        on the Git server, with write access if you will push.
      </li>
      <li><strong>A public repository</strong> can be registered without a credential, but you can only read from it.</li>
      <li>The FlowWeaver server must be able to reach the Git host.</li>
    </ul>
  </section>

  <section id="concepts">
    <h2>Concepts and limits</h2>
    <dl>
      <dt>Working copy</dt>
      <dd>
        One clone per repository on the server, under the configured Git root
        (<code>Git:Root</code>, default <code>./data/git</code>). It is created on the first
        operation that needs it. The page, workflows and the agent all share the same copy.
      </dd>
      <dt>Current branch</dt>
      <dd>
        The branch the working copy has checked out. <strong>Switching branch on the detail
        page changes it for everyone</strong>, including workflow steps that don't name a
        branch.
      </dd>
      <dt>Serialized operations</dt>
      <dd>
        Operations on one repository run one at a time inside a server process. Several
        server replicas are <em>not</em> coordinated with each other.
      </dd>
      <dt>File size</dt>
      <dd>
        Reading or writing a file larger than <code>Git:MaxFileBytes</code> (default 5 MiB)
        fails with <code>file exceeds max size (…)</code> or
        <code>content exceeds max size (…)</code>.
      </dd>
      <dt>Author</dt>
      <dd>
        The author name is the step's <code>author_name</code> if it sets one, otherwise the
        username of whoever the commit runs as — yours from the page, and
        <code>workflow-runner</code> for a workflow step, because that is the name the worker
        runs under. Only when there is no username at all does it fall back to
        <code>Git:DefaultAuthorName</code> (default <code>FlowWeaver</code>). The email is
        <code>Git:DefaultAuthorEmail</code> (default <code>flowweaver@localhost</code>)
        unless <code>author_email</code> is given; there is no username-derived email.
      </dd>
    </dl>

    <h3>Allowed URLs</h3>
    <ul>
      <li><code>https://…</code></li>
      <li><code>ssh://…</code></li>
      <li>scp style <code>user@host:path</code></li>
    </ul>
    <p>
      <code>http://</code>, <code>git://</code> and <code>file://</code> are rejected. The form
      lets <code>http://</code> through, but the server then refuses it. The message depends
      on which call you made: registering a repository answers
      <code>url must be https:// (ssh and git:// are not supported)</code>, while editing an
      existing one answers the shorter <code>url must be https://</code>. Both come from the
      same check, and despite either wording SSH <em>is</em> supported; only plain HTTP and
      <code>git://</code> are not.
    </p>

    <h3>How authentication is chosen</h3>
    <table>
      <thead><tr><th>URL</th><th>Credential</th><th>Result</th></tr></thead>
      <tbody>
        <tr><td>HTTPS</td><td>A credential with a token in its password</td><td>Username and password (token) authentication.</td></tr>
        <tr><td>HTTPS</td><td>None</td><td>Anonymous. Public repositories only.</td></tr>
        <tr><td>SSH</td><td>Credential with Auth method <code>key</code></td><td>Uses the git command line with the private key and passphrase.</td></tr>
        <tr><td>SSH</td><td>None, or a password credential</td><td>Clone fails with <code>SSH URL requires a credential with auth_method='key' and a private key</code>.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Repository list (<code>/integrations/git</code>)</h3>
    <p>
      Header buttons: <strong>Refresh</strong> and <strong>New repository</strong>. The table
      columns are:
    </p>
    <dl>
      <dt>Name</dt><dd>Click it to open the detail page. The description, if any, appears underneath.</dd>
      <dt>URL</dt><dd>HTTPS URLs get an icon that opens the remote in a new tab.</dd>
      <dt>Branch</dt><dd>The default branch.</dd>
      <dt>Auth</dt><dd><em>authed</em> when a credential is linked, <em>public</em> when not.</dd>
      <dt>Last fetched</dt><dd>When the copy was last cloned or pulled, or <em>never</em>.</dd>
      <dt>Actions</dt><dd><strong>Pull</strong>, <strong>Edit</strong>, <strong>Delete</strong>. <strong>Pull</strong> here always pulls the <em>default</em> branch and leaves it checked out in the working copy.</dd>
    </dl>
    <p>The list loads up to 100 repositories, sorted by name.</p>

    <h3>New repository / Edit dialog</h3>
    <dl>
      <dt>Name</dt><dd>Required. It must be unique among active repositories. A workflow step can use it instead of the id.</dd>
      <dt>Default branch</dt><dd>Defaults to <code>main</code>. This branch is cloned, and it is used when an operation doesn't name one.</dd>
      <dt>URL</dt><dd>Required. See <a href="#concepts">allowed URLs</a>.</dd>
      <dt>Auth credential</dt>
      <dd>
        Only compatible credentials are listed. SSH URLs show key credentials; other URLs
        show credentials whose type contains <code>git</code>. <em>— public / read-only —</em>
        means none. If nothing matches, the dialog says <em>No matching credential found.</em>
        and links to <code>/credentials</code>.
      </dd>
      <dt>Description (optional)</dt><dd>Free text.</dd>
    </dl>
    <p>
      The button is <strong>Register</strong> when creating and <strong>Save changes</strong>
      when editing. Saving a registration does not contact the remote.
    </p>

    <h3>Detail page (<code>/integrations/git/&lt;id&gt;</code>)</h3>
    <dl>
      <dt>Header</dt><dd><strong>Back</strong>, <strong>Pull</strong>, <strong>Push</strong>, <strong>Refresh</strong>. Pull and push act on the branch shown in the file panel.</dd>
      <dt>Files → branch selector</dt><dd>Lists local and remote branches. Picking one <strong>checks it out</strong> in the shared working copy. A branch that exists only on the remote gets a local tracking branch.</dd>
      <dt>Files → tree</dt><dd>Breadcrumb starting at <em>root</em>. Folders come first; files show their size in bytes.</dd>
      <dt>Files → viewer</dt>
      <dd>
        Syntax-highlighted content with <strong>Edit</strong>. While editing:
        <strong>Cancel</strong> and <strong>Commit…</strong> (enabled once you change
        something; a <em>modified</em> badge appears). Binary files show a <em>binary</em>
        badge and cannot be edited.
      </dd>
      <dt>Commit changes dialog</dt>
      <dd>
        <strong>Commit message</strong> (pre-filled <code>update &lt;path&gt;</code>) and
        <strong>Push to origin/&lt;branch&gt; after commit</strong> (on by default). Only the
        open file is written and committed.
      </dd>
      <dt>Inbound webhooks</dt><dd>See <a href="#webhooks">Webhooks</a>.</dd>
    </dl>
  </section>

  <section>
    <h2>Main procedure (admin): connect a test repository</h2>
    <ol>
      <li>Create the credential in <a href="/credentials"><code>/credentials</code></a>. For HTTPS: Type <code>git_token</code>, username, and the token as password.</li>
      <li>Go to <code>/integrations/git</code> → <strong>New repository</strong>.</li>
      <li>Fill in <strong>Name</strong> (e.g. <code>lab-configs</code>), <strong>URL</strong>, <strong>Default branch</strong>, and pick the credential. Click <strong>Register</strong>.</li>
      <li>
        <strong>Test the connection:</strong> there is no dedicated test button. Click
        <strong>Pull</strong> on the row. It clones the repository if needed and fetches.
        <em>Pulled · UpToDate</em> (or <em>FastForward</em>) means the URL, credential and
        branch work. An error toast shows the Git error.
      </li>
      <li>Open the repository. The file tree should list the branch contents.</li>
      <li>To check write access: open a harmless file, <strong>Edit</strong>, change it, <strong>Commit…</strong> with push on, then confirm the commit on the remote.</li>
    </ol>
    <Callout tone="warning" title="Confirm the push on the remote">
      After <strong>Commit</strong> with push on, the page shows <em>Committed and pushed</em>
      even if the push failed. The server still reports success because the local commit
      worked. Check the remote, or click <strong>Push</strong> in the header: that shows
      <em>Pushed</em> or <em>Push: &lt;reason&gt;</em>.
    </Callout>
  </section>

  <section>
    <h2>Using a repository in a workflow (operator)</h2>
    <p>
      Git work in a workflow runs through the built-in snippet <code>git</code> (type
      <code>git</code>, target mode <code>once</code>). Drag it from the editor palette. If
      it has never completed a run, it is under <em>Unproven</em>. Click the node and fill
      <strong>Parameters</strong>, using the form or the JSON view. The step reads everything
      from the node config; the snippet's own code is ignored.
    </p>

    <h3>Node config keys</h3>
    <table>
      <thead><tr><th>Key</th><th>Used by</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td><code>operation</code></td><td>all</td><td>Required. One of <code>read_file</code>, <code>write_file</code>, <code>commit</code>, <code>pull</code>, <code>push</code> (case-insensitive).</td></tr>
        <tr><td><code>repository_id</code></td><td>all</td><td>The repository's UUID, from the detail page URL.</td></tr>
        <tr><td><code>repository</code></td><td>all</td><td>Alternative to <code>repository_id</code>: the repository <strong>name</strong> (or a UUID). Takes precedence when both are set. Portable across instances.</td></tr>
        <tr><td><code>path</code></td><td>read_file, write_file</td><td>Required there. Relative to the repository root. <code>..</code> and <code>.</code> segments are rejected.</td></tr>
        <tr><td><code>ref</code></td><td>read_file</td><td>Branch, tag or commit SHA to read from. Defaults to the currently checked-out branch.</td></tr>
        <tr><td><code>content</code></td><td>write_file</td><td>The full new file content. Templates are resolved first.</td></tr>
        <tr><td><code>commit_message</code></td><td>write_file, commit</td><td>Required there.</td></tr>
        <tr><td><code>branch</code></td><td>write_file, pull, push</td><td>For write_file it checks the branch out first, creating it if needed. For pull and push it is the target branch. Pull defaults to the default branch; push and write_file default to the current branch.</td></tr>
        <tr><td><code>push</code></td><td>write_file, commit</td><td>Boolean. Push after committing.</td></tr>
        <tr><td><code>paths</code></td><td>commit</td><td>Array of paths to stage. If omitted, everything is staged.</td></tr>
        <tr><td><code>author_name</code>, <code>author_email</code></td><td>write_file, commit</td><td>Override the commit author.</td></tr>
      </tbody>
    </table>
    <p>
      <code>commit</code> ignores <code>branch</code> and commits on whatever is checked out.
      Use <code>write_file</code> with <code>branch</code>, or a <code>pull</code> step with
      <code>branch</code> first, to make sure you are on the right branch.
    </p>

    <h3>Outputs</h3>
    <ul>
      <li><code>read_file</code> → <code>path</code>, <code>ref</code>, <code>content</code>, <code>size</code>, <code>is_binary</code>. Binary content is base64.</li>
      <li><code>write_file</code>, <code>commit</code>, <code>pull</code>, <code>push</code> → <code>ok</code>, <code>commit_sha</code>, <code>branch</code>, <code>message</code>.</li>
    </ul>
    <p>
      The step fails when <code>ok</code> is false: a pull with conflicts
      (<code>message</code> <code>Conflicts</code>), or a push rejected by the remote. As in
      the UI, a <code>write_file</code> or <code>commit</code> with <code>push: true</code>
      <strong>succeeds even if the push failed</strong>. The failure only appears in
      <code>message</code> (<code>committed · push failed: …</code>). Branch on the message,
      or use a separate <code>push</code> step, which does fail.
    </p>
    <p>
      If nothing changed, <code>write_file</code> and <code>commit</code> succeed with
      <code>message</code> <code>no changes</code> and no <code>commit_sha</code>.
    </p>
    <Callout tone="info" title="Rollback tier">
      The <code>git</code> step defaults to <em>requires compensation</em>. A pushed commit
      is not undone automatically. If you need to back out, add a revert on a
      <code>failure</code> edge.
    </Callout>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Goal:</strong> save a device's running configuration into
      <code>lab-configs</code> and push it.
    </p>
    <p><strong>Prerequisites:</strong></p>
    <ul>
      <li>The repository <code>lab-configs</code> is registered and <strong>Pull</strong> succeeds.</li>
      <li>The workflow has an upstream ssh node <code>show_run</code> whose output <code>stdout</code> holds the config.</li>
    </ul>
    <p>Node config for the <code>git</code> step (placeholders in angle brackets):</p>
    <pre><code>{`{
  "operation": "write_file",
  "repository": "lab-configs",
  "branch": "main",
  "path": "configs/<device-name>.cfg",
  "content": "{{ steps.show_run.output.stdout }}",
  "commit_message": "backup: <device-name>",
  "push": true
}`}</code></pre>
    <p>
      <strong>Expected result:</strong> the step completes with
      <code>message</code> <code>committed · pushed</code> (or <code>no changes</code> if the
      file was identical). The commit is visible on the remote and in the Files panel after a
      pull.
    </p>
    <p>
      <strong>Common error:</strong> the step fails with
      <code>not_found: no git repository named 'lab-configs' is registered on this instance…</code>.
      <strong>Recovery:</strong> check the spelling against the Name column (it must match an
      active repository), or use <code>repository_id</code> with the UUID from the detail
      page URL.
    </p>
  </section>

  <section id="webhooks">
    <h2>Inbound webhooks</h2>
    <p>
      A webhook lets GitHub, GitLab or any other sender tell FlowWeaver about a push. On a
      push, FlowWeaver can pull the repository, start a workflow, or both.
    </p>

    <h3>Add webhook dialog</h3>
    <dl>
      <dt>Name</dt><dd>Required.</dd>
      <dt>Provider</dt>
      <dd>
        <em>GitHub (HMAC-SHA256)</em> checks <code>X-Hub-Signature-256: sha256=&lt;hex&gt;</code>.
        <em>GitLab (token)</em> compares <code>X-Gitlab-Token</code> with the secret.
        <em>Generic (HMAC-SHA256)</em> checks <code>X-FlowWeaver-Signature: sha256=&lt;hex&gt;</code>.
      </dd>
      <dt>Run workflow on push</dt><dd>Optional. The dropdown lists up to 200 workflows with their environment.</dd>
      <dt>Secret</dt><dd>Encrypted at rest and never shown again. When editing, leave it blank to keep the stored one.</dd>
      <dt>Branch filter (empty = all)</dt>
      <dd>
        Add branch names one at a time with <strong>Enter</strong> or <strong>+</strong>.
        Matching is <strong>exact and case-sensitive</strong>. Wildcards such as
        <code>release/*</code> are <em>not</em> expanded, despite the placeholder.
      </dd>
      <dt>Auto-pull on push</dt><dd>On by default. Pulls the pushed branch before the workflow starts. A pull failure does not stop the workflow from starting.</dd>
      <dt>Enabled</dt><dd>A disabled webhook rejects deliveries (HTTP 403).</dd>
      <dt>Allow unsigned (testing only)</dt><dd>Accepts deliveries when <em>no secret is stored</em>. Without a secret and without this flag, every delivery is rejected.</dd>
    </dl>

    <h3>Connecting the provider</h3>
    <ol>
      <li>Save the webhook. Its card shows the ingestion URL, <code>https://&lt;your-host&gt;/api/git/webhooks/&lt;webhook-id&gt;</code>. Copy it with the copy icon.</li>
      <li>GitHub: add a webhook with that URL, content type <code>application/json</code>, and the same secret. GitLab: add a webhook with that URL and the same value in <em>Secret token</em>.</li>
      <li>Generic senders must POST JSON with header <code>X-FlowWeaver-Event: push</code>, a <code>ref</code> of the form <code>refs/heads/&lt;branch&gt;</code>, and the signature header.</li>
    </ol>
    <p>
      The URL is built from the host your browser used. If FlowWeaver sits behind a proxy,
      check that the URL is reachable from the provider.
    </p>

    <h3>What happens on delivery</h3>
    <ol>
      <li>Unknown id → 404. Disabled → 403, recorded as <em>rejected</em>.</li>
      <li>Wrong signature → 401 <code>signature mismatch</code>. No secret and unsigned not allowed → 401.</li>
      <li>An event other than push (e.g. GitHub's <code>ping</code>) → 200 <em>verified</em>, nothing else happens.</li>
      <li>A branch outside the filter → 200 <em>verified</em> with <code>branch '&lt;name&gt;' not in filter</code>.</li>
      <li>500 or more queued jobs → 503 <code>queue full, retry later</code>, recorded as <em>rejected</em>.</li>
      <li>Otherwise: optional pull, then optional run with trigger <code>webhook</code> → 202 <em>dispatched</em>. If the run could not be enqueued → 500 <em>failed</em>, with the reason.</li>
    </ol>
    <p>The started workflow receives this input:</p>
    <pre><code>{`{
  "git_webhook_id": "<uuid>",
  "repository_id": "<uuid>",
  "branch": "main",
  "commit_sha": "<sha or null>",
  "provider": "github"
}`}</code></pre>
    <p>
      Read these as <code>{'{{ input.branch }}'}</code> and so on. Because run input is merged
      into every step's config, a <code>git</code> step in that workflow already has a
      <code>repository_id</code>. A <code>repository_id</code> or <code>repository</code> set
      on the node takes precedence.
    </p>

    <h3>Deliveries</h3>
    <p>
      <strong>Deliveries</strong> on a webhook card opens <em>Recent deliveries</em> with the
      last 50 entries. Each shows the status (<em>dispatched</em>, <em>verified</em>,
      <em>rejected</em>, <em>failed</em>), time, event, branch, short SHA, the error, and
      <strong>View run →</strong> when a run was started. The card also shows the latest
      delivery time and status. Each delivery is also written to the
      <a href="/docs/admin/audit">audit log</a> as
      <code>git_webhook.ingest.&lt;ok|rejected|unknown|backpressure|error&gt;</code>
      with actor <code>git-webhook</code>.
    </p>
    <p>
      Limits: request body up to 1 MiB (413 <code>webhook_too_large</code>), and 30 requests
      per minute per source IP and webhook. An oversized body leaves no trace: the size check
      answers <code>413</code> before the audit row is written, so the only sign of it is on
      the sender's side.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <table>
      <thead><tr><th>Action</th><th>Capability</th><th>Built-in role</th></tr></thead>
      <tbody>
        <tr><td>List, view, browse files, list branches, diff</td><td><code>git.read</code></td><td>viewer and above</td></tr>
        <tr><td>Register, edit, delete; pull, push; switch branch; commit</td><td><code>git.manage</code></td><td>admin</td></tr>
        <tr><td>List webhooks and deliveries</td><td><code>gitwebhook.read</code></td><td>viewer and above</td></tr>
        <tr><td>Create, edit, delete webhooks</td><td><code>gitwebhook.manage</code></td><td>admin</td></tr>
      </tbody>
    </table>
    <ul>
      <li>The detail page shows <strong>Pull</strong>, <strong>Push</strong>, the branch selector and <strong>Edit</strong> to everyone. Without <code>git.manage</code> Pull, Push and the branch selector fail with a permission error. <strong>Edit</strong> is different: it only opens the file in a textarea in your browser and calls nothing, so it always appears to work — the permission error arrives when you click <strong>Commit</strong>.</li>
      <li>
        A <code>git</code> step whose <code>operation</code> writes can only be saved by
        someone holding <code>git.manage</code> — the editor, the import wizard and the
        assistant all refuse it otherwise, naming the node. The rule is by exclusion
        rather than by list: <code>read_file</code> is the only value that counts as a
        read, so <code>write_file</code>, <code>commit</code>, <code>pull</code>,
        <code>push</code>, a <code>{'{{ … }}'}</code> template and even a misspelled
        operation are all treated as writes and gated. A node that has no
        <code>operation</code> set at all — the key missing, or its value
        <code>null</code> or blank — is not gated, because there is nothing there yet to
        classify; it is caught later, at run time, as an unknown operation. The step
        itself runs unattended, so anyone allowed to run the saved workflow can trigger
        the write it contains.
      </li>
      <li>Tokens and keys stay in the credential store and are never returned. Repository create, update and delete are audited with the URL and credential id, never the secret.</li>
      <li>The ingestion endpoint needs no login; the signature is the only protection. Keep <strong>Allow unsigned</strong> off outside testing.</li>
      <li>HTTPS is required for token authentication so the token never travels in clear text.</li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li><strong>No repositories:</strong> <em>No Git repositories yet</em> with <strong>Register first repository</strong>.</li>
      <li><strong>List failed:</strong> a red alert with the error, plus <em>Couldn't load repositories</em>.</li>
      <li><strong>Detail loading:</strong> <em>Loading repository…</em>. The first open of a repository clones it, which can take a while for large repositories.</li>
      <li><strong>Empty folder or empty repository:</strong> the tree shows <em>empty</em>.</li>
      <li><strong>No file selected:</strong> <em>Select a file from the tree to view or edit.</em></li>
      <li><strong>No webhooks:</strong> <em>No webhooks configured. Add one and paste the URL into your Git provider's webhook settings.</em></li>
      <li><strong>No deliveries:</strong> <em>No deliveries recorded yet.</em></li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>Message</th><th>Cause</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr><td><code>Name and URL are required.</code></td><td>An empty field in the dialog.</td><td>Fill both.</td></tr>
        <tr><td><code>URL must be https://… or an SSH form (git@host:path or ssh://…).</code></td><td>The form rejected the URL shape.</td><td>Use HTTPS or an SSH form.</td></tr>
        <tr><td><code>url must be https:// (ssh and git:// are not supported)</code> on register, or <code>url must be https://</code> on edit</td><td>The server rejected the URL, typically <code>http://</code>. The two messages are the same check worded differently.</td><td>Switch to <code>https://</code>, or use an SSH form — which is accepted despite the longer message.</td></tr>
        <tr><td><code>a repository with that name already exists</code> (409)</td><td>Duplicate name.</td><td>Choose another name.</td></tr>
        <tr><td><code>auth_credential_id not found</code></td><td>The credential was deleted.</td><td>Pick another credential.</td></tr>
        <tr><td><code>SSH URL requires a credential with auth_method='key' and a private key</code> / <code>SSH URL needs a key credential</code></td><td>An SSH URL without a key credential.</td><td>Link a key credential.</td></tr>
        <tr><td><code>git operation failed: …</code> (500)</td><td>A Git error: authentication, unreachable host, unknown branch, and so on.</td><td>Read the text. Check the token or key, its scopes, and the branch name.</td></tr>
        <tr><td><em>Pull: Conflicts</em></td><td>The remote and the working copy diverged.</td><td>No merge tool is available in the UI. Resolve on the remote (for example, revert the conflicting commit), then pull again. If the local commits are disposable, an admin can re-register the repository under a new id to get a fresh clone.</td></tr>
        <tr><td><em>Push: branch '&lt;x&gt;' not found locally</em></td><td>The branch was never checked out in the working copy.</td><td>Select the branch, or pull it, first.</td></tr>
        <tr><td><em>Push: push failed: …</em></td><td>Rejected by the remote: non-fast-forward, protected branch, or missing permission.</td><td>Pull first, or use a branch you may push to.</td></tr>
        <tr><td><code>path '…' not in tree</code> (404) / <code>path '…' is not a file</code></td><td>Wrong path or ref.</td><td>Check the path in the Files panel on that ref.</td></tr>
        <tr><td><code>unknown git operation '…'</code></td><td>A typo in <code>operation</code>.</td><td>Use one of the five operations.</td></tr>
        <tr><td><code>repository is required — the repository's name, or repository_id as a UUID</code></td><td>Neither key set, or not a valid UUID.</td><td>Set one of them.</td></tr>
        <tr><td><code>provider must be github, gitlab, or generic</code></td><td>Bad provider.</td><td>Pick one from the list.</td></tr>
        <tr><td><code>on_push_workflow_id not found</code></td><td>The chosen workflow no longer exists.</td><td>Pick another workflow.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Impact, reverting and recovery (admin)</h2>
    <ul>
      <li><strong>Editing URL or credential</strong> takes effect on the next operation. The existing clone is kept and is <em>not</em> re-pointed at a new URL, so a changed URL only takes effect after a fresh clone (see delete below).</li>
      <li><strong>Switching branch</strong> on the detail page changes the branch that workflow steps without <code>branch</code> operate on. Switch back when you are done, or always set <code>branch</code> in steps.</li>
      <li><strong>Delete</strong> asks for confirmation (<em>Delete repository "…"?</em>). The registration is soft-deleted and <strong>workflows that reference it fail</strong>. The clone on disk is kept, but it is tied to the old id: registering the same URL again creates a new id and a fresh clone, and workflows using <code>repository_id</code> must be updated. Workflows using <code>repository</code> (name) keep working once a repository with that name exists.</li>
      <li><strong>Deleting a webhook</strong> stops FlowWeaver from acting on deliveries, but the provider keeps sending until you remove the webhook there too. The confirmation dialog says so.</li>
      <li><strong>Editing a webhook cannot clear <em>Run workflow on push</em>.</strong> Choosing <em>— don't run a workflow —</em> keeps the previous workflow. Delete the webhook and create a new one instead.</li>
      <li>Every repository create, update and delete is in the <a href="/docs/admin/audit">audit log</a> (entity <code>git_repository</code>).</li>
    </ul>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>No dedicated connection test; use <strong>Pull</strong>.</li>
      <li>No merge or conflict-resolution UI, and no diff view on the page (the diff API exists for the agent).</li>
      <li>A commit with push on reports success even when the push failed.</li>
      <li>Branch filters are exact names; no wildcards.</li>
      <li>One shared working copy per repository; branch switches affect everyone.</li>
      <li>Several server replicas can corrupt a working copy if they operate on it at the same time.</li>
      <li>The page lists at most 100 repositories, and the webhook workflow picker at most 200 workflows.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/credentials">Credentials</a> — tokens and SSH keys used for authentication.</li>
      <li><a href="/docs/snippets">Snippets</a> — the <code>git</code> snippet type.</li>
      <li><a href="/docs/workflows">Workflows</a> — templates and node configuration.</li>
      <li><a href="/docs/runs">Runs</a> — runs started by webhooks (trigger <code>webhook</code>).</li>
      <li><a href="/docs/admin/audit">Audit log</a> — repository changes and webhook deliveries.</li>
    </ul>
  </section>
</DocLayout>
