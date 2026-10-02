<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Integrations"
  lead="External REST systems your workflows and the AI agent can call. Each integration bundles a base URL, credentials, a catalog of discoverable actions, and optional prompt skills and API specs for the assistant."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/integrations"><code>/integrations</code></a>.
    Sidebar → <strong>Integrate</strong> → <strong>Integrations</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      An integration is a named connection to a third-party REST API — NetBox,
      ServiceNow, Infoblox, Palo Alto, or any generic REST service. It carries
      three things:
    </p>
    <ul>
      <li>
        <strong>Connection</strong> — base URL, authentication method, TLS
        settings, and a readable name.
      </li>
      <li>
        <strong>Actions</strong> — individual endpoints on that API, discovered
        by uploading an OpenAPI spec or registered manually. Each action can be
        dropped onto a workflow as a node or fired ad-hoc from the test dialog.
      </li>
      <li>
        <strong>Skills and specs</strong> — optional prompt-skill Markdown files
        and OpenAPI documents that teach the AI assistant how to reason about
        this integration.
      </li>
    </ul>
  </section>

  <section>
    <h2>Integration types</h2>
    <p>
      The <code>type</code> is chosen at creation time and drives the colour of
      the badge in the list. It's also used by the backend to pick the right
      auth builder and action discovery adapter.
    </p>
    <dl>
      <dt>netbox</dt>
      <dd>NetBox DCIM / IPAM. Token auth.</dd>
      <dt>servicenow</dt>
      <dd>ServiceNow tables API. Basic auth is typical.</dd>
      <dt>infoblox</dt>
      <dd>Infoblox WAPI. Basic auth.</dd>
      <dt>paloalto</dt>
      <dd>Panorama / PAN-OS. API key auth.</dd>
      <dt>generic_rest</dt>
      <dd>
        Anything else. Any auth method. Pair with a hand-written OpenAPI spec
        to get discoverable actions.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Authentication methods</h2>
    <p>
      Six options, chosen per-integration: <code>none</code>, <code>token</code>,
      <code>bearer</code>, <code>basic</code>, <code>api_key</code> and
      <code>oauth2_client_credentials</code>. Secrets are stored server-side;
      plaintext values are never returned. To rotate a secret, expand the row,
      re-enter the new value in the edit form and click <em>Save</em> — the next
      call uses it. Then run a <a href="#health-check">health check</a> to confirm
      the new value is accepted.
    </p>
    <dl>
      <dt>none</dt>
      <dd>
        No credentials injected. Useful for internal microservices that are
        reachable without auth. The auth config is stored as an empty object.
      </dd>
      <dt>token</dt>
      <dd>
        Sends <code>Authorization: Token &lt;token&gt;</code> — the literal
        <code>Token</code> prefix (NetBox's scheme). Stored as
        <code>{'{method: "token", token: "…"}'}</code>.
      </dd>
      <dt>bearer</dt>
      <dd>
        Sends <code>Authorization: Bearer &lt;token&gt;</code>. Use this for
        <strong>Slack</strong>, GitHub, and most modern APIs that expect a
        Bearer token. Stored as <code>{'{method: "bearer", token: "…"}'}</code>.
      </dd>
      <dt>basic</dt>
      <dd>
        HTTP basic auth. Requires a <em>Username</em> and <em>Password</em>.
      </dd>
      <dt>api_key</dt>
      <dd>
        API key header or query parameter, per the integration type's adapter.
      </dd>
      <dt>oauth2_client_credentials</dt>
      <dd>
        Real OAuth2: the backend calls your identity provider's <em>Token
        URL</em> with <code>grant_type=client_credentials</code> (client id +
        secret, optional scope) and sends the returned access token as
        <code>Authorization: Bearer …</code>. Tokens are cached in memory
        until shortly before expiry and re-granted automatically — nothing to
        rotate by hand, and the token itself is never stored in the
        integration. Use this for APIs fronted by Keycloak, Auth0, Entra ID
        and similar. The token URL passes the same SSRF guard as every other
        integration URL. (For a static, pre-issued OAuth token, use
        <strong>bearer</strong> instead.)
      </dd>
    </dl>
    <Callout tone="warning" title="TLS">
      The <em>Skip TLS verification</em> checkbox disables certificate
      validation. Turn it on only for lab instances with self-signed certs;
      production should use properly issued certificates.
    </Callout>
  </section>

  <section>
    <h2>The list page</h2>
    <p>
      Route: <code>/integrations</code>. Header with a <em>New integration</em>
      button. If there are no integrations yet, an empty-state card
      appears with the same button.
    </p>

    <h3>Row layout</h3>
    <p>
      Each integration is a collapsible card. The collapsed header shows, left
      to right:
    </p>
    <ul>
      <li>A coloured dot reflecting the current health <code>status</code> (hover for the raw value).</li>
      <li>The integration name.</li>
      <li>A tone-coded badge with the <code>type</code>.</li>
      <li>The <code>base_url</code> in monospace (hidden on narrow screens).</li>
      <li>A chevron that rotates when the card is expanded.</li>
    </ul>
    <p>
      Clicking the header toggles the expanded view. Only one card can be open
      at a time — clicking a different header closes the first.
    </p>

    <h3>Expanded content</h3>
    <p>
      Four sections stacked vertically inside the expanded card:
    </p>
    <ol>
      <li>
        <strong>Action buttons</strong> — <em>Health check</em> (green,
        heart-rate icon), <em>Permissions</em> (per-integration role grants;
        see <a href="/docs/permissions">Permissions</a>) and <em>Delete</em>
        (red, trash icon).
      </li>
      <li>
        <strong>Edit integration</strong> — an inline form for name, base URL,
        auth method + fields, health-check path, the TLS-skip checkbox and the
        private-network toggle.
      </li>
      <li>
        <strong>Skills &amp; specs</strong> — <em>Load skill</em> and
        <em>Load spec</em> buttons that attach a prompt skill or an OpenAPI
        spec to this integration.
      </li>
      <li>
        <strong>Actions</strong> — the discovered action catalog (see below).
      </li>
    </ol>
  </section>

  <section>
    <h2>Edit form (inside an expanded row)</h2>
    <p>
      When you expand a row, the edit form pre-populates from the stored auth
      configuration. The auth method is inferred from the shape:
    </p>
    <ul>
      <li><code>{'{}'}</code> → <strong>none</strong>.</li>
      <li><code>method: "oauth2_client_credentials"</code> → <strong>oauth2_client_credentials</strong>.</li>
      <li><code>method: "bearer"</code> (or <code>"oauth2"</code>) → <strong>bearer</strong>.</li>
      <li>Has <code>username</code> → <strong>basic</strong>.</li>
      <li>Has <code>api_key</code> → <strong>api_key</strong>.</li>
      <li>Otherwise → <strong>token</strong>.</li>
    </ul>

    <h3>Fields</h3>
    <dl>
      <dt>Name</dt><dd>Display name. Changing it updates every reference in the UI immediately; action ids stay stable.</dd>
      <dt>Base URL</dt><dd>Full origin + optional path prefix. Actions concatenate their <code>path</code> onto this URL.</dd>
      <dt>Auth method</dt><dd>Select from the methods above. The field row on the right re-renders to show the right inputs.</dd>
      <dt>Token / Username / Password / API key / Token URL + Client ID + Client secret + Scope</dt><dd>Secret fields for the chosen method. Type is <code>password</code> so values are masked.</dd>
      <dt>Health check path (optional) / Expected status</dt><dd>Explicit probe for the <a href="#health-check">health check</a>. The path must be relative.</dd>
      <dt>Skip TLS verification</dt><dd>Checkbox.</dd>
      <dt>Allow private-network targets</dt><dd>Allows the integration to reach 10/8, 172.16/12 and 192.168/16 addresses (for example an internal NetBox or AWX). Loopback and cloud metadata addresses stay blocked. Changing it asks for a reason (required when enabling), which is recorded in the audit log.</dd>
    </dl>
    <p>
      The <em>Save</em> button writes the new config. No confirmation dialog.
    </p>
    <p>
      There is no separate detail page: a link to <code>/integrations/&lt;id&gt;</code>
      (used, for example, in import warnings) opens this list with that
      integration expanded.
    </p>
  </section>

  <section id="health-check">
    <h2>Health check</h2>
    <p>
      Clicking <em>Health check</em> fires a probe to the integration's base URL
      using the configured credentials. When the integration has credentials,
      the check also tries to <strong>verify them</strong> — reachability alone
      is not enough: many services (NetBox among them) answer 200 on their root
      to anyone, whatever the token. The checker probes the API root derived from the
      integration's registered actions and compares authenticated vs anonymous
      responses. The result updates both the <code>status</code> field and the
      dot colour on the row header. Outcomes:
    </p>
    <ul>
      <li><strong>healthy</strong> — green dot. Reachable; if credentials are configured and could be exercised, they were accepted (<code>auth_verified: true</code>).</li>
      <li><strong>degraded</strong> — yellow dot. Two ambiguous cases: (a) credentials are configured but no probed endpoint ever validated them (everything answers anonymously) — an invalid token would go unnoticed; fix by pointing <code>health_check</code> at an authenticated endpoint, e.g. for NetBox <code>{'{'}"path": "/api/", "expected_status": 200{'}'}</code>. (b) No credentials configured and the probed path answers 401/403 — fine if the endpoints you actually use are open; point <code>health_check.path</code> at one of them, or add credentials.</li>
      <li><strong>unhealthy</strong> — red dot. Configured credentials rejected (401/403), 5xx, DNS error, connection refused, timeout. Details go to the toast.</li>
    </ul>
    <p>
      Configuring an explicit <code>health_check</code> (path + expected status)
      switches the probe to strict mode: exactly that path, exactly that status,
      nothing else — pointing it at an authenticated endpoint is the most
      reliable way to make token failures show up as unhealthy.
    </p>
  </section>

  <section>
    <h2>Deleting an integration</h2>
    <p>
      Confirm dialog. On confirm:
    </p>
    <ul>
      <li>The integration is removed.</li>
      <li>All its discovered actions are removed too.</li>
      <li>Scoped prompt skills and API specs stay in the catalog but lose their link to this integration (rebind them manually if needed).</li>
      <li>Any workflow node still referencing one of this integration's actions will fail to resolve at run time until the node is repointed.</li>
    </ul>
  </section>

  <section>
    <h2>Actions catalog</h2>
    <p>
      The last section inside an expanded row. A table listing
      every registered action for this integration.
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Method</td><td>HTTP method. Badge colour: GET=green, POST=blue, PUT/PATCH=yellow, DELETE=red.</td></tr>
        <tr><td>Path</td><td>Monospace path relative to the integration's <code>base_url</code>. Path parameters appear in <code>{'{curly}'}</code> braces.</td></tr>
        <tr><td>Name</td><td>Human-readable operation name (from the OpenAPI <code>operationId</code> or equivalent).</td></tr>
        <tr><td>Category</td><td>Grouping hint from the OpenAPI <code>tags</code> — used to organize the workflow editor palette.</td></tr>
        <tr><td>Actions</td><td>Test (play icon) and Delete (trash).</td></tr>
      </tbody>
    </table>

    <h3>Empty catalog</h3>
    <p>
      When no actions are registered yet, the section shows the message
      <em>"No actions yet. Click Load spec above to attach an OpenAPI spec and materialize its operations as actions."</em>
    </p>
  </section>

  <section>
    <h2>Test action dialog</h2>
    <p>
      Opened by clicking the play icon on a row. An extra-large dialog with
      three JSON textareas and a live response pane.
    </p>

    <h3>Inputs</h3>
    <dl>
      <dt>body (JSON)</dt>
      <dd>The request body. Serialised as-is. Defaults to <code>{'{}'}</code>.</dd>
      <dt>path params (JSON)</dt>
      <dd>Values for the action's <code>{'{braces}'}</code> segments. Keys must match the braces in the path.</dd>
      <dt>query (JSON)</dt>
      <dd>Key/value pairs appended as the URL query string.</dd>
    </dl>

    <h3>Output</h3>
    <p>
      After clicking <em>Run test</em>:
    </p>
    <ul>
      <li>
        <strong>Resolved URL</strong> — the final URL with base, path params, and
        query string applied. Useful to sanity-check templating.
      </li>
      <li>
        <strong>HTTP status badge</strong> — green for 2xx/3xx, red for 4xx/5xx.
        Transport errors (timeouts, DNS) render as a red alert instead of a
        status code.
      </li>
      <li>
        <strong>Response body</strong> — full body as returned, in a scrollable
        block capped at 256 px.
      </li>
      <li>
        <strong>Applied request headers (redacted)</strong> — collapsible
        <code>&lt;details&gt;</code> element. Secrets are replaced with
        <code>***</code> so screenshots don't leak credentials.
      </li>
    </ul>
    <Callout tone="info" title="Nothing is persisted">
      Test-action runs don't create a <code>step_run</code> or write to the
      audit log. They exist purely for inspection and iteration.
    </Callout>
    <Callout tone="admin" title="Role gate">
      Testing an action needs <code>integration.test</code> (Operator and
      Admin by default). Viewers see the button but get a 403 error.
    </Callout>
  </section>

  <section>
    <h2>New integration dialog</h2>
    <p>
      Opened from the page header's <em>New integration</em> button. An extra-large
      modal with three tabs. Validation runs when you click submit — if a field
      is invalid, the dialog switches to the tab where the problem is and shows
      an inline error.
    </p>

    <h3>Tab 1 — Integration</h3>
    <dl>
      <dt>Name</dt><dd>Required.</dd>
      <dt>Type</dt><dd>Required. Select among <code>netbox</code>, <code>servicenow</code>, <code>infoblox</code>, <code>paloalto</code>, <code>generic_rest</code>.</dd>
      <dt>Base URL</dt><dd>Required. Full origin plus any API prefix.</dd>
      <dt>Description</dt><dd>Optional.</dd>
      <dt>Auth method</dt><dd>One of the six methods above: <code>none</code>, <code>token</code>, <code>bearer</code>, <code>basic</code>, <code>api_key</code>, <code>oauth2_client_credentials</code>. The rest of the form re-renders based on this.</dd>
      <dt>Token / Username / Password / API key</dt><dd>Masked inputs. Required for the matching method: a token for <code>token</code> and <code>bearer</code>, username and password for <code>basic</code>, an API key for <code>api_key</code>.</dd>
      <dt>Token URL / Client ID / Client secret / Scope</dt><dd>Only for <code>oauth2_client_credentials</code>. <em>Token URL</em> and <em>Client ID</em> are required; <em>Scope</em> is optional.</dd>
      <dt>Skip TLS verification</dt><dd>Checkbox.</dd>
    </dl>

    <h3>Tab 2 — Skills</h3>
    <p>
      Stage one or more Markdown prompt skills that the AI assistant will load
      alongside its system prompt whenever it touches this integration.
    </p>
    <ul>
      <li>Click the upload button and pick <code>.md</code> files. Each becomes a staged row.</li>
      <li>Filenames are used as skill names verbatim. They must match <code>^[a-zA-Z0-9_\-]+\.md$</code> — alphanumerics, underscore, hyphen, with a <code>.md</code> suffix.</li>
      <li>Duplicate names in the same upload batch block submission.</li>
      <li>Edit the content inline before submit if needed. Remove with the trash button.</li>
      <li>Nothing is persisted until you submit the whole dialog.</li>
    </ul>

    <h3>Tab 3 — Specs</h3>
    <p>
      OpenAPI/YAML specs the agent can inspect. Uploading a spec is also how you
      populate the Actions catalog for this integration — the backend parses
      the spec after creation and creates one action per path+method. Later,
      <em>Load spec</em> on the expanded card does the same for an existing
      integration.
    </p>
    <ul>
      <li>Upload <code>.yaml</code> or <code>.yml</code> files.</li>
      <li>The spec id defaults to the filename stem in lower case; it must match <code>^[a-zA-Z0-9_\-]+$</code>.</li>
      <li>Validation also rejects anything that doesn't contain a top-level <code>paths:</code> key, since that's the minimum an OpenAPI spec needs to be useful.</li>
    </ul>

    <h3>Submit</h3>
    <p>
      All three tabs are saved together in a single request. On success:
    </p>
    <ul>
      <li>The new integration appears in the list.</li>
      <li>A toast reports how many skills and specs were linked.</li>
      <li>The dialog resets and closes.</li>
    </ul>
  </section>

  <section>
    <h2>Wiring an action into a workflow</h2>
    <ol>
      <li>Open any draft workflow at <code>/workflows/{'{id}'}</code>.</li>
      <li>In the left palette, expand <em>Integrations</em>, find your integration, expand a category, locate the action.</li>
      <li>Drag the action card onto the canvas. A node with <code>snippet_id: integration_action</code> is created.</li>
      <li>Click the new node. The dialog shows path-param inputs, query inputs, and a body textarea.</li>
      <li>Fill them in with literals or <code>{'{{steps.prev.output.field}}'}</code> references. Save the workflow.</li>
    </ol>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — read integrations and the action catalog. The Test and Health check buttons are visible but the server rejects the call with 403.</li>
      <li><strong>Operator</strong> — full CRUD, health checks, test actions, and attaching skills and specs to an integration (New integration dialog or expanded card). The standalone AI skills and AI specs pages are admin-only.</li>
      <li><strong>Admin</strong> — same as operator.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — how actions become nodes.</li>
      <li><a href="/docs/ai/skills">AI skills</a> — managing prompt skills after they've been uploaded.</li>
      <li><a href="/docs/ai/specs">AI specs</a> — managing API specs after upload.</li>
    </ul>
  </section>
</DocLayout>
