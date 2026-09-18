<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Troubleshooting"
  lead="Start from what you see, not from where it happens. Each row names the most likely causes and points to the chapter with the full procedure."
>
  <Callout tone="where" title="Where to find it">
    Manual → <strong>Reference</strong> → <strong>Troubleshooting</strong>.
  </Callout>

  <Callout tone="info" title="First, check the connection banner">
    If pages look empty or actions fail silently, look for the strip above the
    layout that says the frontend lost contact with the backend. Nothing below
    applies until it is gone.
  </Callout>

  <section>
    <h2>Signing in</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>Correct password is rejected after several attempts</td><td>The account is locked after repeated failures. An admin can check the <em>Auth</em> tab of the <a href="/docs/admin/audit">Audit log</a> for a <code>lockout</code> event.</td></tr>
        <tr><td>Forgotten password</td><td>There is no self-service reset. Ask an admin to set a new one from <a href="/docs/admin/users">Users</a>.</td></tr>
        <tr><td>Account cannot sign in at all</td><td>The account is disabled. An admin re-enables it in <a href="/docs/admin/users">Users</a>.</td></tr>
        <tr><td>Every tab is suddenly signed out</td><td>A reused refresh token was treated as theft and all sessions were revoked. Sign in again; see <a href="/docs/account">Account &amp; session</a>.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Saving or running a workflow</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td><em>Save failed</em> in the editor toolbar</td><td>The graph needs at least one real node, <em>Start</em> connected to the first node and the last node connected to <em>End</em>. Hover the indicator for the exact error. See <a href="/docs/workflows">Workflows</a>.</td></tr>
        <tr><td>Save or promote is blocked with a reason you did not write</td><td>A <a href="/docs/policies">policy</a> matched. The error carries the policy's reason; an admin can find it on the policy audit page.</td></tr>
        <tr><td><code>production_immutable</code></td><td>Production workflows cannot be edited. Use <em>Clone</em> to get a draft, edit it and promote again.</td></tr>
        <tr><td>Production promotion is rejected</td><td>No successful QA run in the last 48 hours, or <em>Approved by</em> is missing. See <a href="/docs/qa-lab">QA lab</a>.</td></tr>
        <tr><td>A <code>{'{{ … }}'}</code> template fails the step</td><td>Wrong node id, a field the producer never emits, or a <code>device.*</code> reference in a <code>once</code> node. Run <em>Simulate</em> first. See <a href="/docs/workflows">Workflows</a> → Variable references.</td></tr>
        <tr><td>A node says its snippet, action or pool cannot be resolved</td><td>The referenced snippet, integration action or pool was deleted. Repoint the node.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Devices and targets</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td><code>409 no runnable targets in environment '…'</code></td><td>Every selected device or pool has that environment switched off. Light the matching icon in <a href="/docs/devices">Devices</a> or <a href="/docs/device-pools">Device pools</a>.</td></tr>
        <tr><td>A device is greyed out in the run dialog</td><td>It does not allow the workflow's environment. Same fix as above.</td></tr>
        <tr><td><code>credential 00000000-… not found</code> or <code>credential not found</code></td><td>The device has no credential, or its credential was deleted. Assign one in <a href="/docs/devices">Devices</a> (bulk-assign works).</td></tr>
        <tr><td>SSH step fails with a host key mismatch</td><td>The device presented a different key than the pinned one. If the rotation is legitimate, clear the fingerprint on the device; the next connect re-pins it.</td></tr>
        <tr><td>An SSH step stops with a policy denial before a command runs</td><td>A policy with <code>ssh_command_regex</code> matched that command. See <a href="/docs/policies">Policies</a>.</td></tr>
        <tr><td>An SSH step warns that a command is not in the catalogue</td><td>The <a href="/docs/vendor-commands">vendor command catalogue</a> has no entry for it on that platform. It is a warning only; the command still runs.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Integrations</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>Health check is <em>unhealthy</em></td><td>Credentials rejected (401/403), a 5xx, DNS or connection failure. The toast has the detail. See <a href="/docs/integrations">Integrations</a> → Health check.</td></tr>
        <tr><td>Health check is <em>degraded</em></td><td>The probe could not tell whether the credentials work. Point <code>health_check</code> at an authenticated endpoint.</td></tr>
        <tr><td>Test action returns 403 for you but works for others</td><td>Your role or permissions do not include testing actions. See <a href="/docs/permissions">Permissions</a>.</td></tr>
        <tr><td>Slack API answers <code>ok=false</code> with an authentication error</td><td>The integration uses <code>token</code> (prefix <code>Token</code>); Slack needs <code>bearer</code>.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Runs</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>Run stays <code>pending</code></td><td>No worker is picking up jobs. The <em>Job queue</em> card on the <a href="/docs/dashboard">Dashboard</a> and the <a href="/docs/admin">Admin dashboard</a> show a growing pending count; ask the operator of the deployment to check the worker.</td></tr>
        <tr><td>Run finished as <code>failed</code></td><td>Read the failure banner on the monitor, then the step's error and logs. <em>Fix with AI</em> hands the context to the assistant. See <a href="/docs/runs">Runs</a>.</td></tr>
        <tr><td>Monitor does not update live</td><td>The WebSocket failed; the page falls back to polling every 2 s. Reload if it stays stale.</td></tr>
        <tr><td>Step output has escape sequences</td><td>The node sets <code>preserve_ansi: true</code>, or you are reading <code>stdout_raw</code>.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Webhooks</h2>
    <p>
      Rejected webhook calls are covered response by response in
      <a href="/docs/workflows/triggers">Triggers and webhooks</a> → Errors and
      recovery. The usual suspects: a signature computed over a different body
      than the one sent, a disabled trigger, and bursts cut by the rate limiter
      (<code>429</code>). Target devices in the body are ignored unless the
      trigger allows overriding them.
    </p>
  </section>

  <section>
    <h2>Messages and email</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>The bot never answers</td><td>Inbound traffic is not reaching FlowWeaver, or the sender is not linked. Check the channel's <em>Details</em> panel. See <a href="/docs/ai/channels">Messaging channels</a> → Troubleshooting.</td></tr>
        <tr><td>The bot answers with a broken link-account URL</td><td><code>MESSAGING_PUBLIC_BASE_URL</code> is empty or wrong.</td></tr>
        <tr><td>Teams replies fail with <code>teams AAD token failed: 401</code></td><td>Client secret expired or wrong, or the tenant id is missing.</td></tr>
        <tr><td>Email test fails</td><td>Read the message: authentication, TLS/port mismatch, unreachable host, SSRF block or rejected sender. See <a href="/docs/email">Email</a> → Testing.</td></tr>
        <tr><td><code>email_send</code> step is green but nobody got the mail</td><td>The relay accepted it; check spam and the provider's logs. Branch on <code>{'{{ steps.X.output.ok }}'}</code> to catch explicit failures.</td></tr>
        <tr><td><code>slack_message</code> step returns <code>ok=false</code></td><td>Slack reports errors in the body, not as HTTP errors. The bot must be a member of the channel.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Python snippets</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>"disallowed module" at run time</td><td>The module is not on the allow-list, or its pip package is not <code>ready</code> yet. See <a href="/docs/admin/python-packages">Python packages</a>.</td></tr>
        <tr><td>Package stuck in <code>failed</code></td><td>The install failed (often no wheel available). Read the error on the row. See <a href="/docs/admin/python-packages">Python packages</a>.</td></tr>
        <tr><td>Step fails with <code>integration_not_authored</code></td><td>A <code>*_integration_id</code> value came from the run input or a template. Write the integration id directly in the node's config. See <a href="/docs/snippets">Snippets</a>.</td></tr>
        <tr><td>Script cannot open a socket</td><td>Snippets have no network by default. Only an admin can mark a snippet <a href="/docs/snippets">network-enabled</a>.</td></tr>
        <tr><td>Snippet is rejected on save as a scaffold</td><td>The code contains a placeholder marker ("TODO: implement", "not yet implemented", …) and does no real work. Write the real body.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Permissions</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>A page loads but its data or actions fail with 403</td><td>The page is hidden for your role, not blocked in the browser; the server rejects the calls. Ask an admin.</td></tr>
        <tr><td>A tool call in chat fails with 403</td><td>Your role or grants do not cover that tool. Channels can only lower your access, never raise it.</td></tr>
        <tr><td>Access changed after an admin edited settings</td><td>The permissions overlay or RBAC mode was switched. See <a href="/docs/admin/settings">Application settings</a> and <a href="/docs/permissions">Permissions</a>.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Missing data in admin views</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause and fix</th></tr></thead>
      <tbody>
        <tr><td>Traces page is empty</td><td>The window or filters exclude everything. Widen <em>Window</em> first, then clear <em>Slower than</em>. See <a href="/docs/admin/traces">Traces</a>.</td></tr>
        <tr><td>Failed sign-ins for unknown usernames are missing from the audit log</td><td>Unattributed failures are hidden by default. See <a href="/docs/admin/audit">Audit log</a>.</td></tr>
        <tr><td>Audit filters return nothing for automation</td><td>Automation has no user id; filter by <em>Actor</em> (e.g. <code>workflow-webhook</code>) instead of <em>User</em>.</td></tr>
        <tr><td>SLO page shows no data</td><td>Nothing ran in the selected window. See <a href="/docs/admin/slo">Service-level objectives</a>.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/reference">Reference</a> — screens, routes and roles.</li>
      <li><a href="/docs/glossary">Glossary</a> — the terms used above.</li>
    </ul>
  </section>
</DocLayout>
