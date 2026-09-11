<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Email"
  lead="Configure the SMTP relays your workflows send mail through — Gmail, Microsoft 365, SendGrid, Amazon SES, Mailgun or your own server — and send with the email_send node."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Integrate</strong> → <strong>Email</strong>
    (<a href="/email"><code>/email</code></a>).
  </Callout>
  <Callout tone="admin" title="Admin only">
    Creating and editing channels requires the <code>admin</code> role — a channel
    holds an SMTP password. Firing a test message needs <code>email.send</code>,
    which an operator has, so an operator can verify a relay without being able to
    repoint it.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      An <strong>email channel</strong> is one SMTP account: a host, a port, an
      encryption mode, a credential and the address mail goes out as. Workflows
      never carry any of that — an <a href="/docs/snippets"><code>email_send</code></a>
      node names a channel (or falls back to the default one) and supplies only the
      message.
    </p>
    <p>
      That split is what lets you rotate a password, move from a personal Gmail to a
      corporate relay, or point staging at a catch-all mailbox without touching a
      single workflow.
    </p>
  </section>

  <section>
    <h2>Adding a channel</h2>
    <p>
      Click <strong>New channel</strong> and pick a provider. The connection fields
      fill themselves in — you supply the credentials and the sender address, then
      save.
    </p>

    <h3>Providers</h3>
    <dl>
      <dt>gmail</dt>
      <dd>
        <code>smtp.gmail.com:587</code>, STARTTLS. The password must be a 16-character
        <strong>App Password</strong> — Google rejects the account password over SMTP.
        The account needs 2-Step Verification enabled before App Passwords appear.
      </dd>
      <dt>outlook365</dt>
      <dd>
        <code>smtp.office365.com:587</code>, STARTTLS. The username is the mailbox UPN.
        <strong>SMTP AUTH must be enabled for that mailbox</strong> in Exchange Online —
        it is off by default on new tenants, and shows up as an authentication failure.
      </dd>
      <dt>sendgrid</dt>
      <dd>
        <code>smtp.sendgrid.net:587</code>, STARTTLS. The username is always the literal
        <code>apikey</code> (the form locks it), and the password is an API key with the
        Mail Send permission.
      </dd>
      <dt>ses</dt>
      <dd>
        <code>email-smtp.&lt;region&gt;.amazonaws.com:587</code>, STARTTLS. The preset points at
        <code>us-east-1</code> — change the host if your verified identity lives in another
        region. Credentials are IAM <em>SMTP</em> credentials, not an access key pair.
      </dd>
      <dt>mailgun</dt>
      <dd>
        <code>smtp.mailgun.org:587</code>, STARTTLS. Username and password come from the
        domain's SMTP credentials page.
      </dd>
      <dt>smtp</dt>
      <dd>
        A custom server — an in-house Postfix, an Exchange on-prem, a corporate relay.
        Nothing is pre-filled. Leave username and password empty for an unauthenticated
        internal relay.
      </dd>
    </dl>

    <h3>Encryption and ports</h3>
    <p>The mode has to match the port, or the connection fails during the handshake:</p>
    <ul>
      <li><strong>STARTTLS</strong> (port 587) — connects in the clear and upgrades. What nearly every provider expects.</li>
      <li><strong>SSL/TLS</strong> (port 465) — encrypted from the first byte.</li>
      <li><strong>None</strong> (port 25) — unencrypted. Only appropriate for an internal relay.</li>
    </ul>
    <Callout tone="warning" title="Plaintext auth is refused">
      Saving a channel with a username, a password and encryption set to
      <strong>None</strong> is rejected unless <strong>Allow private network</strong> is
      also on. Otherwise the credential would go out in the clear to a public host.
    </Callout>

    <h3>Sender identity</h3>
    <p>
      <strong>From address</strong> is required and must be an identity the provider
      recognises as yours — SES needs it verified, Microsoft 365 needs it to be the
      authenticated mailbox or one it can send-as. A mismatch is rejected by the
      server, not by FlowWeaver. <strong>From name</strong> and <strong>Reply-To</strong>
      are optional.
    </p>

    <h3>Default channel</h3>
    <p>
      One channel can be marked <strong>default</strong>. An <code>email_send</code> node
      with no <code>channel_id</code> uses it. Marking a new channel default demotes the
      previous one, so there is never more than one.
    </p>
  </section>

  <section>
    <h2>Testing</h2>
    <p>
      The paper-plane button on a row sends a real message. Do this before wiring a
      workflow: a wrong password fails identically at run time, but here you see the
      SMTP server's own words.
    </p>
    <p>Common failures and what they mean:</p>
    <ul>
      <li><strong>SMTP authentication failed</strong> — wrong credentials, or the provider wants an app password / API key rather than the account password.</li>
      <li><strong>TLS handshake failed</strong> — the encryption mode does not match the port (587 wants STARTTLS, 465 wants SSL/TLS).</li>
      <li><strong>Cannot reach the SMTP server</strong> — wrong host, or egress on that port is blocked. Providers commonly block port 25.</li>
      <li><strong>SSRF blocked</strong> — the host resolves to a private address. Turn on <strong>Allow private network</strong> for a deliberately internal relay.</li>
      <li><strong>SMTP command rejected</strong> — the server accepted you but refused the message; usually the From address is not an identity you own.</li>
    </ul>
    <p>
      Every attempt, successful or not, is written to the audit log and updates the
      row's <strong>Last send</strong> column.
    </p>
  </section>

  <section>
    <h2>Sending from a workflow</h2>
    <p>
      Drop an <code>email_send</code> node on the canvas and fill its config. Only
      <code>to</code> and <code>subject</code> are required, plus at least one of
      <code>body</code> / <code>html</code>:
    </p>
    <pre><code>{`{
  "to": "noc@example.com",
  "cc": ["oncall@example.com"],
  "subject": "Backup finished on {{ device.name }}",
  "body": "{{ steps.backup.output.summary }}",
  "attachments": [
    {
      "file_name": "report.pdf",
      "content_base64": "{{ steps.report.output.content_base64 }}",
      "content_type": "application/pdf"
    }
  ]
}`}</code></pre>
    <p>
      <code>to</code>, <code>cc</code> and <code>bcc</code> each accept a single address,
      a comma-separated string, or an array. Supplying only <code>html</code> generates a
      plain-text alternative automatically — many relays penalise HTML-only mail.
      Attachments take base64, which is exactly what an upstream
      <code>report</code> step produces.
    </p>
    <p>
      Add <code>channel_id</code> to pin the node to a specific relay; omit it to use the
      default channel.
    </p>

    <h3>Reading the result</h3>
    <p>
      The node outputs <code>ok</code>, <code>message_id</code>, <code>recipients</code>,
      <code>elapsed_ms</code> and <code>error</code>. Branch on
      <code>{'{{ steps.X.output.ok }}'}</code>.
    </p>

    <Callout tone="warning" title="Not reversible">
      A delivered email cannot be recalled, so <code>email_send</code> is marked
      <strong>non-reversible</strong>. Promotion to production warns about it, and
      rollback refuses to roll back a workflow whose graph contains one. Put the send
      last, after everything that can still fail.
    </Callout>
  </section>

  <section>
    <h2>Security</h2>
    <ul>
      <li>Passwords are encrypted at rest with the deployment keyring and never returned to the UI — the API exposes only <code>has_password</code>. Leaving the field blank on an edit keeps the stored one.</li>
      <li>The SMTP host goes through the same SSRF guard as integration URLs. Private ranges are blocked unless the channel opts in; loopback and the cloud metadata IP stay blocked either way.</li>
      <li><strong>Skip TLS verification</strong> exists for an internal relay with a self-signed certificate. On a public provider it removes the guarantee that you are talking to the real server, and the credential can be captured by anyone able to intercept.</li>
      <li>Step logs record the recipient <em>count</em>, not the addresses, so a trace never leaks a distribution list.</li>
      <li>Create, edit, delete and test are all audited. The audit record carries the channel's posture — host, encryption, sender, the private-network and TLS flags — never the password.</li>
    </ul>
  </section>
</DocLayout>
