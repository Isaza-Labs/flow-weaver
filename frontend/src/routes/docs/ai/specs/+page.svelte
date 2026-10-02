<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="AI API specs"
  lead="OpenAPI 3.x YAML documents the agent can inspect. They feed the dynamic tools list_apis / discover_operations / operation_detail / execute_operation."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/ai/specs"><code>/ai/specs</code></a>.
    Sidebar → <strong>Intelligence</strong> → <strong>AI</strong> → Agent
    composition card → <em>API specs</em>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      An <strong>API spec</strong> is a stored OpenAPI 3.x YAML document. The
      assistant's built-in <em>dynamic tools</em> read these specs so you can
      teach the agent about a new third-party API without writing any code:
    </p>
    <ul>
      <li><code>list_apis</code> — enumerates available spec ids.</li>
      <li><code>discover_operations</code> — lists the operations in a given spec.</li>
      <li><code>operation_detail</code> — returns parameters, bodies, and security for one operation.</li>
      <li><code>execute_operation</code> — actually fires the call through the appropriate integration's auth.</li>
    </ul>
    <p>
      Specs can be <strong>global</strong> (discoverable from any conversation)
      or scoped to a specific <a href="/docs/integrations">integration</a>,
      in which case the agent will prefer that integration's credentials
      when executing operations from the spec.
    </p>
  </section>

  <section>
    <h2>Fields</h2>
    <dl>
      <dt>api</dt>
      <dd>
        Short string id the agent sees. Must match
        <code>^[a-zA-Z0-9_\-]+$</code> (no extension). Example:
        <code>netbox</code>, <code>servicenow-itsm</code>.
      </dd>
      <dt>content</dt>
      <dd>
        The OpenAPI YAML body. Must contain a top-level <code>paths:</code>
        key — the upload guard rejects anything else up-front so you don't
        find out later.
      </dd>
      <dt>Active</dt>
      <dd>Checkbox. Inactive specs don't show up for the dynamic tools.</dd>
      <dt>Integration</dt>
      <dd>
        Optional. Select one of the configured integrations or leave as
        <em>Global</em>.
      </dd>
    </dl>
  </section>

  <section>
    <h2>List page</h2>
    <p>
      Route: <code>/ai/specs</code>. Header with <em>Refresh</em>,
      <em>Upload</em>, and <em>New spec</em>. Columns include: <code>api</code>
      id, integration scope, operation count, active state, last updated, and
      actions. Operation count comes from the parser — handy to confirm your
      paste parsed correctly.
    </p>

    <h3>Integration filter</h3>
    <p>
      Same filter pattern as the skills page: <em>All</em>, <em>Global</em>,
      or a specific integration name. Useful when you need to double-check
      what the agent knows about a given system.
    </p>

    <h3>Actions per row</h3>
    <dl>
      <dt>Edit</dt><dd>Opens the form dialog — content, active flag, integration scope can all change.</dd>
      <dt>Download</dt><dd>Saves the YAML to your disk.</dd>
      <dt>Delete</dt><dd>Confirms then removes. The dynamic tools stop listing it immediately.</dd>
    </dl>
  </section>

  <section>
    <h2>Upload flow</h2>
    <p>
      Clicking <em>Upload</em> opens a file picker that accepts
      <code>.yaml</code> / <code>.yml</code>. The spec id is derived from
      the filename stem (lowercased), but you can edit before saving. Failed
      uploads keep the dialog open with the error at the top of the form so
      you don't lose the paste.
    </p>
  </section>

  <section>
    <h2>How the agent uses specs</h2>
    <ol>
      <li>
        When a conversation starts, the active specs are exposed
        via <code>list_apis</code>.
      </li>
      <li>
        When the user asks about a specific system (e.g. "list NetBox
        devices"), the agent calls <code>discover_operations</code> on the
        relevant spec, picks an operation, calls
        <code>operation_detail</code> to see required parameters, then calls
        <code>execute_operation</code>.
      </li>
      <li>
        <code>execute_operation</code> resolves credentials server-side —
        the agent never handles keys directly. A spec scoped to an
        integration uses that integration's auth config; a global spec uses
        the <code>x-credential-ref</code> extension on its security scheme,
        typically a reference such as
        <code>{'${secret:secret:netbox-token:value}'}</code>; see
        <a href="/docs/admin/secrets">Secrets</a>.
      </li>
    </ol>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer / Operator</strong> — no access to this screen: the API-specs API is admin-only, whatever the RBAC mode. The agent still uses active specs on everyone's behalf.</li>
      <li><strong>Through an integration</strong> — anyone with <code>integration.manage</code> (operator and above) can attach an OpenAPI spec to an integration when creating or editing it.</li>
      <li><strong>Admin</strong> — full CRUD, upload, download.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai/skills">Skills</a> — the Markdown half of the agent context.</li>
      <li><a href="/docs/integrations">Integrations</a> — where <code>execute_operation</code> gets its credentials.</li>
    </ul>
  </section>
</DocLayout>
