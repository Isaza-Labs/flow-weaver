<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Triggers and webhooks"
  lead="Every way a workflow can start on its own: cron schedules, and signed webhook endpoints that external systems call. This chapter covers the per-workflow Triggers page and the public webhook endpoint."
>
  <Callout tone="where" title="Where to find it">
    Triggers: <code>/workflows/{'{id}'}/triggers</code> ·
    Schedules only: <code>/workflows/{'{id}'}/schedules</code>. Open a workflow in the
    editor and click <strong>More</strong> → <strong>Triggers</strong> or
    <strong>More</strong> → <strong>Schedules</strong>. The page breadcrumbs lead
    back to <strong>Workflows</strong> and to the workflow.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      A <strong>trigger</strong> is a record attached to one workflow that says how
      the workflow can be started. The <strong>Triggers</strong> page ("Every way
      this workflow can be started.") lists all of a workflow's triggers and
      creates the non-schedule ones. Use it to:
    </p>
    <ul>
      <li>give a CI pipeline, monitoring system or ticketing tool a URL that starts the workflow;</li>
      <li>see, enable, disable and delete every trigger in one place;</li>
      <li>get or rotate a webhook's signing secret.</li>
    </ul>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>
        Opening this page needs <code>workflow.read</code> (Viewer and up): it
        reads the triggers through the workflow they belong to, so that is the
        capability the server checks, not <code>trigger.read</code>. (The
        standalone trigger API does gate its reads on <code>trigger.read</code>,
        which in the default role model any viewer has anyway.) Creating,
        enabling, disabling, rotating and deleting need <code>trigger.manage</code>
        (Operator and Admin in the default role model).
      </li>
      <li>
        A webhook fires the workflow in its <strong>current environment</strong>.
        The environment checks a manual run gets when it is queued apply here too.
        For example, a <code>qa</code> workflow only resolves QA-lab devices. See
        <a href="/docs/workflows">Workflows</a>.
      </li>
      <li>
        Decide which devices the webhook should run against before you publish
        the URL. See <a href="#targets">Targets</a>.
      </li>
      <li>
        The sending system must be able to set a custom HTTP header and reach
        FlowWeaver's address (the same origin as the web app).
      </li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>

    <h3>Trigger types</h3>
    <table>
      <thead><tr><th>Type</th><th>What starts the run</th><th>Created from</th></tr></thead>
      <tbody>
        <tr><td><code>schedule</code></td><td>A cron expression in a timezone, fired by the scheduler.</td><td><strong>Schedule</strong> button → <code>/workflows/{'{id}'}/schedules/new</code></td></tr>
        <tr><td><code>webhook</code></td><td>A signed <code>POST</code> to the trigger's public URL.</td><td><strong>Webhook</strong> button (dialog)</td></tr>
        <tr><td><code>manual</code></td><td>Nothing automatic. The row is shown as "Run from UI / API".</td><td><strong>Manual</strong> button (dialog)</td></tr>
        <tr><td><code>event</code></td><td>Nothing yet. Event triggers can be saved but are <strong>not dispatched</strong>.</td><td><strong>Event</strong> button (dialog)</td></tr>
      </tbody>
    </table>
    <p>
      <strong>Schedule vs webhook.</strong> A schedule fires on time with the
      inputs and target devices saved on it. A webhook fires when someone calls
      it, and passes the request body to the run. Both are "unattended": nobody
      clicks <em>Run</em>, and neither goes through the per-user checks a manual
      run does. That is why a webhook's targeting is locked down by default.
    </p>

    <h3>Schedules and this page</h3>
    <p>
      <code>/workflows/{'{id}'}/schedules</code> shows the same data filtered to
      <code>schedule</code> triggers, with columns <strong>Name</strong>,
      <strong>Repeat</strong> (a readable version of the cron),
      <strong>Timezone</strong>, <strong>Next run</strong>, <strong>Last run</strong>
      and <strong>Status</strong>, plus an <strong>Add schedule</strong> button.
      Disabling a schedule there asks for confirmation ("Automated runs will
      stop."). The cron editor, timezone and target-device picker are covered in
      <a href="/docs/schedules">Schedules</a>.
    </p>

    <h3>The webhook URL</h3>
    <p>Every webhook trigger has the path</p>
    <pre><code>{`POST /api/webhooks/workflow/<TRIGGER_ID>`}</code></pre>
    <p>
      The <strong>Webhook endpoint</strong> dialog shows it with the web app's
      origin in front, for example
      <code>https://flowweaver.example.com/api/webhooks/workflow/&lt;TRIGGER_ID&gt;</code>.
      The id is the trigger's id, <strong>not</strong> the workflow's. The endpoint
      is public (no login). Authentication is the trigger's own secret.
    </p>

    <h3>Authentication</h3>
    <p>
      A webhook trigger gets a random signing secret when it is created: 32 random
      bytes written as 64 lowercase hex characters. The secret is stored
      encrypted, and the API returns it only once, in the response to a create or
      a rotate. The one exception is a trigger created with
      <code>allow_unsigned: true</code> — it is created with no secret at all, and
      no secret exists for it until someone rotates one in. A request must carry
      <strong>one</strong> of these headers:
    </p>
    <table>
      <thead><tr><th>Header</th><th>Value</th></tr></thead>
      <tbody>
        <tr>
          <td><code>X-FlowWeaver-Signature</code></td>
          <td>
            <code>sha256=</code> followed by the hex HMAC-SHA256 of the
            <strong>exact raw request body</strong>, keyed with the secret. The
            <code>sha256=</code> prefix and the hex digits are case-insensitive, and
            the hex part must be exactly 64 characters. If this header is absent,
            GitHub's <code>X-Hub-Signature-256</code> is read instead (same format),
            so a GitHub webhook can call the endpoint directly.
          </td>
        </tr>
        <tr>
          <td><code>X-FlowWeaver-Token</code></td>
          <td>The secret itself, as plain text. It must match exactly.</td>
        </tr>
      </tbody>
    </table>
    <p>
      Either header is accepted. The final comparison of the two values is
      constant-time, but it is reached only after a cheap length check that
      returns early — a header of the wrong length is rejected before any
      comparison, so the length of the expected value is not itself hidden. The
      signature
      covers only the body: there is <strong>no timestamp header and no replay
      window</strong>, so anyone who captures a signed request can resend it. Use
      HTTPS, and prefer the signature over the token, because the token puts the
      secret itself on the wire with every request.
    </p>
    <Callout tone="warning" title="Unsigned webhooks">
      A trigger with no secret is refused unless its <code>allow_unsigned</code>
      flag is set, in which case anyone with the URL can fire it. The UI can't set
      this flag. It exists for testing and is set through the API, and only an
      <strong>admin</strong> can switch it on (anyone else gets
      <code>403 allow_unsigned_admin_only</code>); anyone who can manage triggers
      can switch it off. Rotating a secret always turns <code>allow_unsigned</code>
      off again.
    </Callout>

    <h3>What the run receives</h3>
    <p>
      The workflow's <code>input</code> is built from the trigger and the request.
      The request body appears under <code>input.webhook</code>, not at the top
      level:
    </p>
    <pre><code>{`{
  "site": "<value from the trigger's input_defaults>",
  "trigger_id": "<TRIGGER_ID>",
  "webhook": { "device": "core-01", "reason": "<the JSON body exactly as sent>" }
}`}</code></pre>
    <p>
      Here <code>site</code> stands for every key of the trigger's
      <code>input_defaults</code>, and <code>webhook</code> holds whatever JSON object
      the caller sent.
    </p>
    <ul>
      <li>
        In node configs, read body fields as
        <code>{'{{'} input.webhook.&lt;field&gt; {'}}'}</code>. The run's
        <code>run.trigger</code> is <code>webhook</code>.
      </li>
      <li>
        If the body isn't a JSON object (a JSON array, plain text, or invalid
        JSON), the run still fires, with <code>"webhook": null</code>.
      </li>
      <li>
        <code>input_defaults</code> can only be set through the API. The
        <strong>New webhook trigger</strong> dialog doesn't set it.
      </li>
    </ul>

    <h3 id="targets">Targets: <code>target_devices</code>, <code>target_pools</code> and <code>allow_target_override</code></h3>
    <p>
      A webhook caller proves it holds the secret. It doesn't prove it is a user
      allowed to touch a given device. So targeting depends on the trigger:
    </p>
    <table>
      <thead><tr><th><code>allow_target_override</code></th><th>Trigger's <code>target_devices</code></th><th>What the run fires against</th></tr></thead>
      <tbody>
        <tr>
          <td>off (default)</td>
          <td>any</td>
          <td>
            The trigger's <code>target_devices</code>. Any
            <code>target_devices</code> / <code>target_pools</code> in the body are
            <strong>ignored</strong> (and logged).
          </td>
        </tr>
        <tr>
          <td>on</td>
          <td>non-empty</td>
          <td>
            The body can only <strong>narrow</strong> the list: its device ids are
            intersected with the trigger's, and ids outside the list are dropped.
            <code>target_pools</code> in the body is dropped. With no targets in the
            body, the run uses the trigger's list. If <em>none</em> of the body's
            ids are in the list, the run fires with <strong>no</strong> devices; it
            doesn't fall back to the full list.
          </td>
        </tr>
        <tr>
          <td>on</td>
          <td>empty</td>
          <td>
            The body's <code>target_devices</code> and <code>target_pools</code> are
            used <strong>as sent</strong>. The body picks freely.
          </td>
        </tr>
      </tbody>
    </table>
    <p>
      Body targets are top-level arrays of GUID strings. Entries that aren't
      GUIDs are ignored. A trigger created from the dialog has no target devices
      and the override turned off, so its runs have no device context until you
      set targets through the API. See <a href="#procedure">Main procedure</a>,
      step 5.
    </p>

    <h3>Limits</h3>
    <table>
      <thead><tr><th>Limit</th><th>Value</th><th>Response when hit</th></tr></thead>
      <tbody>
        <tr><td>Body size</td><td>1 MiB</td><td><code>413</code></td></tr>
        <tr>
          <td>Rate limit</td>
          <td>20 requests per minute for each <strong>source IP + trigger</strong> pair (fixed window, no queueing)</td>
          <td><code>429</code>, with <code>Retry-After: 60</code></td>
        </tr>
        <tr><td>Job queue</td><td>Refused when 500 or more jobs are pending or claimed</td><td><code>503</code></td></tr>
        <tr><td>Runs per request</td><td>Exactly one run per accepted request. There is no de-duplication or idempotency key: two identical requests start two runs.</td><td>—</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Header buttons</h3>
    <p>
      <strong>Manual</strong>, <strong>Webhook</strong>, <strong>Schedule</strong>
      and <strong>Event</strong>. Schedule opens the schedule form. The others open a
      <strong>New &lt;type&gt; trigger</strong> dialog.
    </p>

    <h3>Tabs</h3>
    <p>
      <strong>All</strong>, <strong>Schedule</strong>, <strong>Webhook</strong>,
      <strong>Manual</strong>, <strong>Event</strong>, each with a count. Rows are
      sorted by type (schedule, webhook, manual, event), then by creation time.
    </p>

    <h3>Table columns</h3>
    <dl>
      <dt>Name</dt><dd>The trigger name, with its description underneath.</dd>
      <dt>Type</dt><dd>A badge with the trigger type.</dd>
      <dt>Detail</dt>
      <dd>
        Webhook: <code>POST /api/webhooks/workflow/&lt;id&gt;</code>. Schedule:
        the cron expression. Manual: "Run from UI / API". Event: the description.
      </dd>
      <dt>Status</dt>
      <dd>
        <strong>disabled</strong> (amber) when the trigger is off. Otherwise the
        latest status value if there is one, or <strong>enabled</strong>. For
        webhooks the status value is one of <code>webhook_dispatched</code>,
        <code>webhook_rejected</code>, <code>webhook_backpressure</code> or
        <code>webhook_failed</code>. All of them are shown in the same green
        badge, so read the text, not the colour.
      </dd>
      <dt>Created</dt><dd>Creation date and time.</dd>
      <dt>Actions</dt>
      <dd>
        <strong>Edit</strong> (schedules only), <strong>Webhook</strong> (webhooks
        only; opens the endpoint dialog), <strong>Enable</strong> /
        <strong>Disable</strong>, <strong>Delete</strong>.
      </dd>
    </dl>

    <h3>New trigger dialog</h3>
    <dl>
      <dt>Name</dt><dd>Required. If it is empty, the dialog shows "Name is required".</dd>
      <dt>Description (optional)</dt><dd>Free text.</dd>
    </dl>
    <p>
      For a webhook, an info box explains that a public POST endpoint and an HMAC
      secret are generated, and that the secret is shown only once. For an event,
      a warning says event triggers aren't dispatched yet.
      <strong>Create trigger</strong> saves the trigger, which starts
      <strong>enabled</strong>.
    </p>

    <h3>Webhook endpoint dialog</h3>
    <dl>
      <dt>Endpoint URL</dt><dd>The full URL, with a <strong>Copy</strong> button, and a reminder of the two auth headers.</dd>
      <dt>Targeting</dt><dd>A description of how this trigger's targets are resolved, based on its current settings.</dd>
      <dt>Signing secret</dt>
      <dd>
        Shown only right after creating or rotating, with <strong>Copy</strong> and
        the warning "Copy this now — it won't be shown again." At any other time
        the dialog says "A signing secret is set (hidden)". If the trigger is
        unsigned it says "Unsigned deliveries are allowed", and if there is no
        secret at all, "No signing secret configured."
      </dd>
      <dt>Close / Rotate secret</dt><dd>Rotate asks for confirmation first.</dd>
    </dl>
  </section>

  <section id="procedure">
    <h2>Main procedure</h2>

    <h3>Create a webhook trigger</h3>
    <ol>
      <li>Open the workflow, then click <strong>More</strong> → <strong>Triggers</strong>.</li>
      <li>Click <strong>Webhook</strong>, enter a <strong>Name</strong> (for example <code>Deploy on push from CI</code>) and click <strong>Create trigger</strong>.</li>
      <li>
        The <strong>Webhook endpoint</strong> dialog opens. Copy the
        <strong>Endpoint URL</strong> and the <strong>Signing secret</strong>, and
        store the secret in the sending system's secret store. After you close
        the dialog, the only way to get a secret again is to rotate it.
      </li>
      <li>
        Configure the sender to <code>POST</code> JSON to the URL with an
        <code>X-FlowWeaver-Signature</code> header (see the worked example), or with
        <code>X-FlowWeaver-Token</code>.
      </li>
      <li>
        <strong>Set the targets</strong> if the workflow acts on devices. There is
        no UI for this yet, so call the trigger API with a bearer access token of a
        user who has <code>trigger.manage</code>:
        <pre><code>{`curl -sS -X PUT "https://<FLOWWEAVER_HOST>/api/WorkflowTrigger/<TRIGGER_ID>" \\
  -H "Authorization: Bearer <ACCESS_TOKEN>" \\
  -H "Content-Type: application/json" \\
  -d '{"target_devices": ["<DEVICE_ID_1>", "<DEVICE_ID_2>"], "allow_target_override": false}'`}</code></pre>
        Only the fields you send are changed. The response is the updated trigger,
        without the secret.
      </li>
      <li>Send a test request and check the response and the <strong>Status</strong> column.</li>
    </ol>

    <h3>Enable or disable</h3>
    <p>
      Click <strong>Disable</strong> or <strong>Enable</strong> on the row. The
      change applies at once. A disabled webhook answers <code>403</code>
      <em>trigger disabled</em>. Senders keep the same URL and secret, so this is
      the way to pause a webhook without breaking its configuration.
    </p>

    <h3>Rotate the secret</h3>
    <ol>
      <li>Click <strong>Webhook</strong> on the row, then <strong>Rotate secret</strong>, and confirm.</li>
      <li>Copy the new secret straight away.</li>
      <li>
        Update the sender. <strong>The old secret stops working immediately</strong>,
        and there is no overlap period, so expect <code>401</code> responses until
        the sender has the new one.
      </li>
    </ol>

    <h3>Delete</h3>
    <p>
      Click <strong>Delete</strong> and confirm ("This cannot be undone."). The
      trigger disappears from the list, and its URL answers <code>404</code>
      <em>webhook trigger not found</em> from then on. Runs already started are not
      affected.
    </p>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Prerequisites:</strong> a workflow whose first step uses
      <code>{'{{'} input.webhook.ticket {'}}'}</code>, and an enabled webhook
      trigger with its URL and secret. Run this from a machine with
      <code>bash</code>, <code>openssl</code> and <code>curl</code>. The values in
      angle brackets are placeholders.
    </p>
    <pre><code>{`#!/usr/bin/env bash
set -euo pipefail

URL="https://<FLOWWEAVER_HOST>/api/webhooks/workflow/<TRIGGER_ID>"
SECRET="<WEBHOOK_SECRET>"   # 64 hex characters, shown once on create/rotate

# Sign the exact bytes you send. Keep the body in one variable.
BODY='{"ticket":"CHG-1234","interface":"ge-0/0/1"}'

SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" | sed 's/^.*= //')

curl -sS -X POST "$URL" \\
  -H "Content-Type: application/json" \\
  -H "X-FlowWeaver-Signature: sha256=$SIG" \\
  --data-binary "$BODY"`}</code></pre>
    <p><strong>Expected result</strong> (HTTP 202):</p>
    <pre><code>{`{
  "ok": true,
  "message": "workflow run enqueued",
  "workflow_run_id": "<RUN_ID>"
}`}</code></pre>
    <p>
      The run appears in <a href="/runs"><code>/runs</code></a> with trigger
      <code>webhook</code>, and the row's status becomes
      <code>webhook_dispatched</code>. To use the token header instead, replace
      the signature header with <code>-H "X-FlowWeaver-Token: $SECRET"</code>.
    </p>
    <p><strong>Common mistake and fix:</strong></p>
    <pre><code>{`{
  "ok": false,
  "message": "signature mismatch",
  "workflow_run_id": null
}`}</code></pre>
    <p>
      The signed bytes differ from the bytes sent. Typical causes are signing a
      pretty-printed file and then sending a re-serialised body, a trailing newline
      (<code>echo</code> adds one; <code>printf '%s'</code> doesn't), or
      <code>curl -d @file</code> stripping newlines. Sign and send the same
      variable with <code>--data-binary</code>, and check that the secret is the
      current one (not from before a rotation).
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li>
        The trigger pages and the <code>/api/WorkflowTrigger</code> API use the
        caller's session. <code>trigger.read</code> lets you list and view;
        <code>trigger.manage</code> lets you create, update, rotate and delete. The
        buttons are visible to everyone who can open the page, and the server
        refuses the action without the capability.
      </li>
      <li>
        The ingest endpoint is anonymous. The run is started by the user
        <code>workflow-webhook</code>, <strong>not</strong> by the person who
        created the trigger, and it skips the environment/device permission check
        a manual run goes through. Anyone holding the secret can start the
        workflow. Treat the secret like a password.
      </li>
      <li>
        Keep <code>allow_target_override</code> off unless the caller really must
        choose targets. If you turn it on, also set <code>target_devices</code>, so
        the body can only narrow that list. An empty list with the override on lets
        any holder of the secret pick any device or pool.
      </li>
      <li>
        The endpoint makes no outbound calls. A URL in the body only matters if the
        workflow uses it, and outbound calls are checked by the SSRF guard when the
        workflow runs.
      </li>
      <li>
        Every request is written to the audit log (<a href="/admin/audit"><code>/admin/audit</code></a>)
        as <code>workflow_webhook.ingest.&lt;status&gt;</code>
        (<code>ok</code>, <code>rejected</code>, <code>unknown</code>,
        <code>backpressure</code>, <code>error</code>), with the source IP, user
        agent, whether the request was signed, and the body size. The body itself
        is not logged. Two kinds of request leave no audit row at all: those the
        rate limiter rejects, and those over the 1 MiB body limit — the size check
        answers <code>413</code> and returns before the row is written, so don't
        go looking for an oversized delivery in the audit log. Creating, updating,
        rotating and deleting a trigger are audited too. The secret never appears
        in any log.
      </li>
      <li>
        Triggers that arrive through a <a href="/docs/workflows/import">bundle
        import</a> are created disabled, without targets, and with a new secret
        you have to rotate to see.
      </li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li>While loading, a large spinner replaces the table.</li>
      <li>No triggers: <em>No triggers yet</em>, or <em>No &lt;type&gt; triggers yet</em> on a type tab, with "Use the buttons above to add one."</li>
      <li>No schedules on <code>/workflows/{'{id}'}/schedules</code>: <em>No schedules yet</em>.</li>
      <li>A load error appears as a dismissible red banner above the tabs.</li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <p>
      The ingest endpoint always answers with
      <code>{'{'} "ok", "message", "workflow_run_id" {'}'}</code>, except for
      <code>413</code> and <code>429</code>, which use the platform's standard
      error bodies.
    </p>
    <table>
      <thead><tr><th>HTTP</th><th>Message</th><th>Meaning</th><th>What to do</th></tr></thead>
      <tbody>
        <tr><td><code>202</code></td><td><em>workflow run enqueued</em></td><td>Accepted. <code>workflow_run_id</code> is set.</td><td>Follow the run in <code>/runs</code>.</td></tr>
        <tr><td><code>401</code></td><td><em>signature mismatch</em></td><td>Wrong signature or token, a missing header, or an old secret.</td><td>Re-check the signing (see the worked example). Rotate if the secret was lost.</td></tr>
        <tr><td><code>401</code></td><td><em>trigger has no secret configured; set one or enable allow_unsigned</em></td><td>The trigger has no secret and doesn't accept unsigned requests.</td><td>Click <strong>Rotate secret</strong> to generate one.</td></tr>
        <tr><td><code>403</code></td><td><em>trigger disabled</em></td><td>The trigger is turned off.</td><td>Click <strong>Enable</strong>.</td></tr>
        <tr><td><code>404</code></td><td><em>webhook trigger not found</em></td><td>The id is unknown or deleted, or the trigger isn't of type <code>webhook</code>.</td><td>Copy the URL again from the <strong>Webhook endpoint</strong> dialog. Use the trigger id, not the workflow id.</td></tr>
        <tr><td><code>413</code></td><td><em>payload too large (&gt;1 MiB)</em></td><td>The body is over 1 MiB.</td><td>Send a reference (an id or URL) instead of the data.</td></tr>
        <tr><td><code>429</code></td><td><code>rate_limited</code>, <code>retry_after_seconds: 60</code></td><td>More than 20 requests in a minute from this IP to this trigger.</td><td>Back off for 60 seconds, and batch or debounce on the sender.</td></tr>
        <tr><td><code>500</code></td><td>The executor's error text</td><td>The run couldn't be queued. Causes include an environment mismatch, targets that don't resolve in this environment, a structural error, an integration still in <code>needs_config</code>, or a sub-workflow cycle. Status <code>webhook_failed</code>.</td><td>Fix what the message names, then resend.</td></tr>
        <tr><td><code>503</code></td><td><em>queue full, retry later</em></td><td>The job queue is backed up (500 or more jobs).</td><td>Retry with backoff. Check the workers.</td></tr>
      </tbody>
    </table>

    <h3>Troubleshooting</h3>
    <table>
      <thead><tr><th>Symptom</th><th>Likely cause</th><th>Fix</th></tr></thead>
      <tbody>
        <tr><td>Every request gets <code>401</code> after a certain time</td><td>Someone rotated the secret; the audit log shows a <code>rotate_secret</code> entry.</td><td>Give the sender the new secret.</td></tr>
        <tr><td><code>401</code> from a GitHub webhook</td><td>GitHub's webhook secret isn't the trigger's secret.</td><td>Paste the trigger's secret into GitHub. <code>X-Hub-Signature-256</code> is accepted.</td></tr>
        <tr><td><code>202</code>, but the steps see no data</td><td>The workflow reads <code>input.&lt;field&gt;</code>.</td><td>Read <code>input.webhook.&lt;field&gt;</code> instead.</td></tr>
        <tr><td><code>202</code>, but <code>input.webhook</code> is null</td><td>The body isn't a JSON object. The endpoint never looks at <code>Content-Type</code>; it simply tries to parse the raw body, and only a JSON <em>object</em> becomes <code>input.webhook</code>. A JSON array, a bare number or string, form-encoded data or an empty body all leave it null, and the request still returns <code>202</code>.</td><td>Send the body as a JSON object (top-level <code>{'{'}…{'}'}</code>). Setting the header changes nothing on its own.</td></tr>
        <tr><td>The run ignored <code>target_devices</code> from the body</td><td><code>allow_target_override</code> is off.</td><td>That is the default. Set targets on the trigger, or turn the override on through the API.</td></tr>
        <tr><td>The run had no devices</td><td>The trigger has no targets, or every body id was outside the trigger's list.</td><td>Set <code>target_devices</code> on the trigger. Only send ids from that list.</td></tr>
        <tr><td>Duplicate runs</td><td>The sender retried after a timeout. There is no de-duplication.</td><td>Make the workflow safe to run twice, or de-duplicate on the sender.</td></tr>
        <tr><td>An <code>api</code>-type row shows "(webhook)" with no URL</td><td>A legacy trigger type. The endpoint only fires <code>webhook</code> triggers.</td><td>Create a new webhook trigger and delete the old row.</td></tr>
        <tr><td>An event trigger never fires</td><td>Event dispatch isn't implemented.</td><td>Use a webhook or a schedule instead.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>
        Target devices, <code>allow_target_override</code>,
        <code>input_defaults</code> and <code>allow_unsigned</code> can't be edited
        in the UI for webhook triggers, and there is no <strong>Edit</strong>
        action for them. Use <code>PUT /api/WorkflowTrigger/{'{id}'}</code>.
      </li>
      <li>No timestamp or replay protection, and no idempotency key.</li>
      <li>Rotating a secret is instant; there is no grace period for the old one.</li>
      <li>The <strong>Status</strong> badge is green for every status value, including <code>webhook_rejected</code> and <code>webhook_failed</code>.</li>
      <li>Only one run per request, and the response doesn't wait for the run to finish.</li>
      <li>Event triggers are stored but never dispatched.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/schedules">Schedules</a>: cron triggers, the global schedule index and the calendar.</li>
      <li><a href="/docs/workflows">Workflows</a>: environments, the editor and template variables such as <code>input</code> and <code>run.trigger</code>.</li>
      <li><a href="/docs/runs">Runs</a>: following the runs a webhook starts.</li>
      <li><a href="/docs/devices">Devices</a> and <a href="/docs/device-pools">Device pools</a>: the ids used in <code>target_devices</code> / <code>target_pools</code>.</li>
      <li><a href="/docs/workflows/import">Importing workflows</a>: what happens to triggers carried in a bundle.</li>
      <li><a href="/docs/admin/audit">Audit log</a>: reviewing requests and secret rotations.</li>
    </ul>
  </section>
</DocLayout>
