<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Snippets"
  lead="Reusable building blocks workflows can call. A snippet is a single unit of work — Python code, an Ansible play, a REST call, a JMESPath transform, a ping, or a stored integration action."
>
  <Callout tone="where" title="Where to find it">
    List: <a href="/snippets"><code>/snippets</code></a> ·
    New: <code>/snippets/new</code> ·
    Editor: <code>/snippets/{'{id}'}</code>. Sidebar → <strong>Build</strong> → <strong>Snippets</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A snippet is a reusable "verb" that a workflow node can invoke. The snippet
      defines the <strong>what</strong> (code, HTTP call, expression), the
      <strong>shape</strong> of its inputs and outputs (JSON schemas), and the
      <strong>execution envelope</strong> (target mode, parallelism, timeout,
      retry policy). Workflow nodes reuse snippets by id; per-node
      <code>config_overrides</code> let each node tweak the behaviour without
      forking the snippet.
    </p>
  </section>

  <section>
    <h2>Snippet types</h2>
    <p>
      The type is chosen at creation time and never changes. Each type unlocks a
      different editor layout on the detail page.
    </p>
    <table>
      <thead>
        <tr><th>Type</th><th>Used for</th><th>Starter template</th></tr>
      </thead>
      <tbody>
        <tr>
          <td><code>python_snippet</code></td>
          <td>Arbitrary Python via <code>flowweaver_runtime</code>. The runner auto-invokes <code>run(ctx)</code> on exit.</td>
          <td>A <code>run(ctx)</code> function calling <code>get_input()</code>/<code>set_output()</code>.</td>
        </tr>
        <tr>
          <td><code>ansible_playbook</code></td>
          <td>YAML playbook executed against targets.</td>
          <td>Minimal play with a <code>ping</code> task.</td>
        </tr>
        <tr>
          <td><code>rest_call</code></td>
          <td>Generic HTTP call built from URL + method + headers + body.</td>
          <td>JSON blob with <code>url</code>, <code>method</code>, <code>headers</code>.</td>
        </tr>
        <tr>
          <td><code>transform</code></td>
          <td>JMESPath expression applied to upstream output.</td>
          <td><code>@</code> (identity).</td>
        </tr>
        <tr>
          <td><code>jmespath</code></td>
          <td>Equivalent to <code>transform</code>; alias kept for migrated content.</td>
          <td><code>@</code>.</td>
        </tr>
        <tr>
          <td><code>ping</code></td>
          <td>ICMP reachability probe against each target device.</td>
          <td><code>{'{"count": 4, "timeout": 5}'}</code>.</td>
        </tr>
        <tr>
          <td><code>integration_action</code></td>
          <td>Reference to an action defined on an <a href="/docs/integrations">integration</a>. Configured per-node in the workflow editor, not here.</td>
          <td><code>{'{}'}</code>.</td>
        </tr>
        <tr>
          <td><code>slack_message</code></td>
          <td>Posts a message to a Slack channel (<code>chat.postMessage</code>). Native built-in: the bot token is a deployment secret (<code>Slack:BotToken</code> / <code>SLACK_BOT_TOKEN</code>), and each node sets <code>channel</code> + <code>text</code> in its config. Slack returns <code>ok=false</code> (not an HTTP error) on failure — branch on <code>output.ok</code>.</td>
          <td><code>{'{ "channel": "#alerts", "text": "…" }'}</code>.</td>
        </tr>
        <tr>
          <td><code>email_send</code></td>
          <td>Sends an email through a configured SMTP relay (see <a href="/docs/email">Email</a>). Native built-in: host, port, TLS and the password live on the channel, and each node sets <code>to</code> + <code>subject</code> + <code>body</code>/<code>html</code> in its config. Omit <code>channel_id</code> to use the default channel. Non-reversible — a sent message cannot be recalled.</td>
          <td><code>{'{ "to": "ops@example.com", "subject": "…", "body": "…" }'}</code>.</td>
        </tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Target modes</h2>
    <dl>
      <dt>per_device</dt>
      <dd>
        The snippet runs once per target device (fan-out). The <em>Run</em>
        dialog on the workflow requires at least one device for workflows
        containing any <code>per_device</code> node.
      </dd>
      <dt>once</dt>
      <dd>
        One execution, no device context. Typical for transforms, REST calls to
        a SaaS API, or Python code that orchestrates without touching a device.
      </dd>
      <dt>per_pool</dt>
      <dd>
        Available in the editor's dropdown. One execution per device pool
        resolved at run time — handy for aggregations that only make sense at
        group level.
      </dd>
    </dl>
  </section>

  <section>
    <h2>The list page</h2>
    <p>
      Route: <code>/snippets</code>. Header with a <em>New snippet</em> button,
      then a search input and a small counter ("N of M"), then a table.
    </p>

    <h3>Search</h3>
    <p>
      Client-side filter matching on <code>name</code>, <code>type</code>, and
      <code>description</code>. The full list is fetched once (up to 500 rows)
      and filtered in memory — no debounce, no roundtrip per keystroke.
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>—</td><td>Chevron toggle. Only present when the snippet has an input schema. Click to expand the row and see the raw <code>input_schema</code> JSON inline.</td></tr>
        <tr><td>Name</td><td>Display name and (if present) the description on a second line. Click the name to open the editor. A dashed <em>unproven</em> chip appears when no step using this snippet has ever completed — those sit in the collapsed <em>Unproven</em> section of the workflow editor palette, so the chip is how you spot the drafts worth finishing or deleting.</td></tr>
        <tr><td>Type</td><td>Neutral badge with the snippet <code>type</code>.</td></tr>
        <tr><td>Target</td><td>The <code>target_mode</code>.</td></tr>
        <tr><td>Parallel</td><td>The <code>max_parallel</code> cap for fan-outs.</td></tr>
        <tr><td>Timeout</td><td>Per-run timeout in seconds.</td></tr>
        <tr><td>Actions</td><td>Edit, Test (deep-links to the editor's <code>#test</code> anchor), Delete.</td></tr>
      </tbody>
    </table>

    <h3>Deleting</h3>
    <p>
      Deletion requires confirmation and cannot be undone. If any workflow
      references the snippet by id, its nodes will fail to resolve until the
      reference is fixed.
    </p>
  </section>

  <section>
    <h2>Create page</h2>
    <p>
      Route: <code>/snippets/new</code>. A single card with:
    </p>
    <dl>
      <dt>Name</dt>
      <dd>Required. Any string.</dd>
      <dt>Description</dt>
      <dd>Optional one-liner.</dd>
      <dt>Type</dt>
      <dd>Select among the seven types listed above.</dd>
      <dt>Target mode</dt>
      <dd>
        <code>per_device</code> or <code>once</code>. You can change to
        <code>per_pool</code> later from the editor.
      </dd>
    </dl>
    <p>
      Submitting creates the snippet with <code>max_parallel: 10</code>,
      <code>timeout_seconds: 300</code>, empty schemas, empty retry policy, and
      the starter template appropriate to the chosen type. You are immediately
      redirected to <code>/snippets/{'{id}'}</code> to edit the code.
    </p>
  </section>

  <section>
    <h2>The editor</h2>
    <p>
      Route: <code>/snippets/{'{id}'}</code>. Two-column layout under a top bar.
    </p>

    <h3>Top bar</h3>
    <p>
      Breadcrumb back to <em>Snippets</em>, the current name, a <code>type</code>
      badge, a save-status indicator, a <em>Test run</em> button, and a
      <em>Save</em> button.
    </p>

    <h3>Left column — type-specific editor (60% width)</h3>
    <p>
      What you see here depends on the snippet type:
    </p>
    <ul>
      <li>
        <strong>python_snippet / ansible_playbook</strong> — a full-pane
        monospace textarea. Tab inserts four spaces. The header shows the line
        count.
      </li>
      <li>
        <strong>transform / jmespath</strong> — the TransformPlayground: an input
        pane, an expression pane, and a live output pane. Save writes the
        expression back as the snippet's <code>code</code>.
      </li>
      <li>
        <strong>rest_call</strong> — a small form: method dropdown (GET / POST /
        PUT / PATCH / DELETE), URL input, a JSON headers textarea, and (for body-bearing methods) a body textarea. These are serialised into the snippet's
        <code>code</code> field as a single JSON blob.
      </li>
      <li>
        <strong>ping</strong> — two number inputs: <em>Count</em> (1–100) and
        <em>Timeout seconds</em> (1–300). Stored as a JSON blob.
      </li>
      <li>
        <strong>integration_action</strong> — a read-only card listing the stored
        integration configuration. Integration action details live on the
        workflow node, not here.
      </li>
      <li>
        <strong>Fallback</strong> — plain code/configuration textarea for anything
        else.
      </li>
    </ul>

    <h3>Right column — settings and schemas (40% width)</h3>
    <p>
      Three scrollable sections:
    </p>
    <h4>Settings</h4>
    <dl>
      <dt>Name</dt><dd>Edit the display name.</dd>
      <dt>Description</dt><dd>Textarea, 3 rows.</dd>
      <dt>Target mode</dt><dd>Select: <code>per_device</code>, <code>once</code>, <code>per_pool</code>.</dd>
      <dt>Max parallel</dt><dd>Integer 1–1000. Caps fan-out concurrency.</dd>
      <dt>Timeout (s)</dt><dd>Integer 1–86400. Engine kills a step that runs longer.</dd>
      <dt>Script language</dt><dd>Python types only. Choose between <code>python</code> and <code>bash</code>.</dd>
      <dt>Network-enabled</dt><dd>
        <code>python_snippet</code> only, <strong>admin-only</strong> checkbox.
        Lifts the sandbox network isolation for interactive SSH — see
        <a href="#network-enabled">Network-enabled snippets</a> below. The same
        toggle also appears in the workflow editor's node dialog (Service tab).
      </dd>
    </dl>

    <h4>Input schema</h4>
    <p>
      JSON Schema used to validate <code>config_overrides</code> on workflow
      nodes and to auto-generate forms. The starter value is an empty object;
      typical content is <code>{'{"type": "object", "properties": { … }}'}</code>.
    </p>

    <h4>Output schema</h4>
    <p>
      Same shape as input schema, describing what the snippet puts into
      <code>step.output_payload</code>. Used by downstream transforms and by
      the AI assistant when reasoning about the DAG.
    </p>

    <h4>Retry policy</h4>
    <p>
      JSON describing how the engine should retry transient failures. Typical
      keys: <code>max_retries</code>, <code>backoff</code>. An empty object means
      no retries.
    </p>

    <Callout tone="warning" title="JSON syntax matters">
      The three JSON textareas are parsed on save. Invalid JSON triggers a
      <em>Save failed</em> state with the parse error in the tooltip — your
      unsaved changes stay in the form until you fix it.
    </Callout>
  </section>

  <section>
    <h2>Test run</h2>
    <p>
      Click <em>Test run</em> (top bar) to open the <strong>SnippetTestDialog</strong>.
      It lets you fire a single execution of the snippet with a synthetic
      payload and optional target devices, then displays the engine response
      without persisting a <code>step_run</code>.
    </p>
    <p>
      Use it to validate code, syntax, and the output schema before wiring the
      snippet into a workflow. For integration actions, prefer the per-action
      test dialog on the <a href="/docs/integrations">Integrations</a> page —
      that path mimics production resolution of headers and path params.
    </p>
  </section>

  <section id="network-enabled">
    <h2>Network-enabled snippets (interactive SSH)</h2>
    <p>
      By default a <code>python_snippet</code> runs in a sandbox with
      <strong>no network</strong> (<code>--unshare-all</code>) and a strict
      import allow-list — it cannot open sockets. That is the right default for
      arbitrary code. But some device operations need a live, <em>interactive</em>
      SSH session that the plain <code>ssh</code> handler can't model — for
      example a password change that prompts <em>"retype new password:"</em> and
      reacts to what the device sends back.
    </p>
    <p>
      For those cases, a snippet can be marked <strong>network-enabled</strong>.
      This single opt-in lifts the network isolation <em>for that one snippet</em>
      so it can use <code>netmiko</code> / <code>paramiko</code> to drive an
      interactive session, while every other snippet stays fully isolated.
    </p>

    <Callout tone="admin" title="Admin-only, by design">
      Only an <strong>admin</strong> can turn network-enabled on, and an admin
      can no longer edit a snippet that is already network-enabled without the
      admin role either. The flag relaxes a security boundary, so it is gated
      end-to-end on the backend — the UI toggle simply mirrors that. Operators
      and viewers see a read-only notice when it is on.
    </Callout>

    <h3>What changes when it's on</h3>
    <ul>
      <li>
        The sandbox gets host networking (<code>--share-net</code>) and DNS
        (<code>/etc/resolv.conf</code>) bound in, instead of the default
        no-network namespace.
      </li>
      <li>
        An extended import allow-list adds the networking libraries
        (<code>netmiko</code>, <code>paramiko</code>, <code>socket</code>, …) on
        top of the normal safe modules — but only for this snippet.
      </li>
      <li>
        Everything else about the sandbox (process/CPU/memory limits, the
        dangerous-builtin checks) is unchanged.
      </li>
    </ul>

    <h3>Turning it on</h3>
    <ol>
      <li>
        On <code>/snippets/{'{id}'}</code> (the snippet editor), in the
        <em>Settings</em> column under <em>Script language</em>, tick
        <strong>Network-enabled · interactive SSH</strong> and save. The control
        is only rendered for admins on <code>python_snippet</code> snippets.
      </li>
      <li>
        Or from the workflow editor: open the node, go to the
        <em>Service</em> tab, and the same checkbox is there.
      </li>
    </ol>

    <Callout tone="warning" title="Never hard-code credentials">
      Don't paste device passwords into the code. Use credential placeholders
      (e.g. <code>{'${secret:credential:<id>:password}'}</code>) so secrets stay
      encrypted and are injected at run time. Network-enabled snippets are the
      most security-sensitive surface on the platform — keep their code
      trusted and reviewed. The <code>time</code> module is allowed so a script
      can pace itself between interactive prompts.
    </Callout>
  </section>

  <section id="extra-imports">
    <h2>Extra imports &amp; packages (admin)</h2>
    <p>
      A <code>python_snippet</code> can only import a fixed safe-list of stdlib
      modules (<code>json</code>, <code>re</code>, <code>datetime</code>, …). When
      a snippet needs more, an <strong>admin</strong> extends the list
      at <a href="/admin/python-packages"><code>/admin/python-packages</code></a>.
    </p>

    <Callout tone="admin" title="Admin-only, sandbox unchanged">
      Only admins manage the list — it relaxes which modules may be imported, so
      the admin is the final barrier. It does <strong>not</strong> lift the rest
      of the sandbox: <code>exec</code>/<code>eval</code>/<code>__import__</code>/<code>open()</code>
      stay blocked, and the snippet still runs with <strong>no network</strong>
      unless it's also <a href="#network-enabled">network-enabled</a>.
    </Callout>

    <h3>Two kinds</h3>
    <dl>
      <dt>stdlib</dt>
      <dd>
        A module already on the interpreter (e.g. <code>statistics</code>,
        <code>decimal</code>). Nothing is installed — it's usable immediately.
      </dd>
      <dt>pip</dt>
      <dd>
        A PyPI package. The worker runs <code>pip install</code> into a
        shared directory, then the module becomes importable. The
        <strong>import name</strong> (what your script writes) and the
        <strong>PyPI package</strong> (what gets installed) can differ — e.g.
        import <code>bs4</code>, install <code>beautifulsoup4</code>; import
        <code>yaml</code>, install <code>PyYAML</code>. Pin a version with a
        spec like <code>Django==5.0</code>.
      </dd>
    </dl>

    <h3>Status</h3>
    <p>
      A pip package shows <code>pending</code> → <code>installing</code> →
      <code>ready</code> (or <code>failed</code> with an error). <strong>Only
      <code>ready</code> modules can be imported</strong>; a snippet that imports
      a not-yet-installed module is rejected at run time with a "disallowed
      module" error. Installs default to wheels-only (no package build code runs);
      an admin can relax that per deployment.
    </p>
  </section>

  <section>
    <h2>Using a snippet from a workflow</h2>
    <ol>
      <li>Open any draft workflow at <code>/workflows/{'{id}'}</code>.</li>
      <li>
        In the left palette under <em>Snippets</em>, find the card with your
        snippet's name. A snippet that has never completed a run lives in the
        collapsed <strong>Unproven</strong> section at the bottom of that list
        — expand it (or just type the name in the search box, which expands it
        for you). It moves up to the main list once a step using it completes.
      </li>
      <li>Drag it onto the canvas. A node with a generated id (<code>node-N</code>) is created.</li>
      <li>Click the node. The dialog shows the <em>Node config</em> section populated from the snippet's input schema.</li>
      <li>Fill in whichever overrides this specific node needs and click <em>Save workflow</em>.</li>
    </ol>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — list and view snippet code.</li>
      <li><strong>Operator</strong> — full CRUD, test runs.</li>
      <li><strong>Admin</strong> — same as operator, plus the admin-gated
        controls: the <a href="#network-enabled">network-enabled</a> flag on
        <code>python_snippet</code> (toggling it on, or editing an
        already-network-enabled snippet) and the
        <a href="#extra-imports">extra-imports</a> list at
        <code>/admin/python-packages</code>.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — where snippets become nodes.</li>
      <li><a href="/docs/integrations">Integrations</a> — the other source of actionable nodes.</li>
    </ul>
  </section>
</DocLayout>
