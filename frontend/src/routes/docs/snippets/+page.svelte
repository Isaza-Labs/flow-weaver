<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Snippets"
  lead="Reusable building blocks workflows can call. A snippet is a single unit of work — Python code, an Ansible play, a REST call, a JMESPath transform, a ping, a stored integration action, a Git operation, a Slack message or an email."
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
          <td><code>git</code></td>
          <td>Read, write, commit, push or pull against a registered <a href="/docs/git-repos">Git repository</a>. The handler reads its parameters (<code>operation</code>, <code>repository_id</code> or <code>repository</code>, <code>path</code>, <code>ref</code>, <code>content</code>, <code>commit_message</code>, <code>branch</code>, <code>push</code>, …) from each node's config, not from the snippet body — the full list is in <a href="/docs/git-repos">Git repositories</a>. Defaults to target mode <code>once</code>.</td>
          <td><code>{'{ "operation": "read_file", "repository_id": "…", "path": "configs/example.cfg", "ref": "main" }'}</code> (documentation only).</td>
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
    <p>
      These are the ten types the <em>Type</em> picker on the create page offers.
      FlowWeaver also ships <strong>built-in snippets</strong> whose handler types
      you cannot pick when creating one — they are seeded at startup and you use
      them as they are (or copy them with <em>Start from</em>):
    </p>
    <ul>
      <li><code>ssh</code> — run one or many commands on a device over SSH (Netmiko), with vendor-aware prompt and paging handling. Target mode <code>per_device</code>.</li>
      <li><code>report</code> — build a downloadable HTML/CSV/XLSX/PDF document; downstream nodes read <code>{'{{ steps.X.output.base64 }}'}</code> and <code>{'{{ steps.X.output.filename }}'}</code>.</li>
      <li><code>mcp_call</code> — call a tool on a registered <a href="/docs/admin/mcp-servers">MCP server</a>; dropped from the palette's <em>MCP</em> section.</li>
    </ul>
    <p>Three terms are easy to mix up:</p>
    <ul>
      <li><strong>Type</strong> — the handler that executes the snippet: one of the ten creatable values above, or a built-in handler type.</li>
      <li><strong>Alias</strong> — <code>jmespath</code> behaves exactly like <code>transform</code>; it is kept so migrated content keeps working.</li>
      <li>
        <strong>Property</strong> — a setting on a snippet of some type, not a type of its own.
        <a href="#network-enabled">Network-enabled</a> is a property of a
        <code>python_snippet</code>. Likewise the seeded SSH baseline (paramiko) is a
        <code>python_snippet</code> with a particular body, which is why it is not in the
        <em>Type</em> list — copy it with <em>Start from</em> instead.
      </li>
    </ul>
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
      Route: <code>/snippets</code>. Header with <em>Import</em>, <em>Export</em> and <em>New snippet</em> buttons,
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
        <tr><td>☐</td><td>Row selection for <em>Export</em>. The header checkbox selects every row the filter shows.</td></tr>
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

    <h3>Export</h3>
    <p>
      <em>Export</em> in the header downloads a JSON bundle
      (<code>{'{'} "format": "flowweaver.snippets", "version": 1, "snippets": [ … ] {'}'}</code>).
      Tick rows to export just those (the button reads <em>Export selected (N)</em>);
      with nothing ticked it exports every snippet the current filter shows.
      Only the fields an author defines travel — name, type, description, code,
      schemas, target mode, parallelism, timeout, retry policy, logic diagram,
      rollback override, <code>changes_state</code> and <code>network_enabled</code>.
      Ids, timestamps, the author, run counters and <code>verified</code> belong to
      this instance and are left out.
    </p>

    <h3>Import</h3>
    <p>
      <em>Import</em> opens a dialog where you choose, drop or paste a bundle. A bare
      array of snippets or a single snippet object is accepted too. The dialog
      previews what will be imported and lists any entries it will ignore (missing
      <code>name</code> or <code>type</code>). When a name already exists you pick
      what happens: import it as a new snippet named <em>… (imported)</em>
      (default), skip it, or overwrite the existing snippet.
    </p>
    <p>
      Each entry goes through the normal create/update endpoints, so it gets the
      same validation as a snippet made by hand and needs the
      <code>snippet.manage</code> permission. A <code>network_enabled</code> snippet
      imported by a non-admin lands with the flag off, and a warning says so. If some
      entries fail, the ones that succeeded stay saved and the dialog lists the reasons
      for the rest.
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
      <dt>Start from</dt>
      <dd>
        Optional. <em>Blank</em> creates a generic starter for the chosen type;
        picking an existing snippet creates a copy of it (body, schemas and
        timeouts), and locks <em>Type</em> and <em>Target mode</em> to the
        source's values. The name defaults to <em>"&lt;source&gt; (copy)"</em>.
        Copying a network-enabled snippet as a non-admin produces a copy with
        network access turned off, and a warning toast says so.
      </dd>
      <dt>Type</dt>
      <dd>Select among the ten types listed above.</dd>
      <dt>Target mode</dt>
      <dd>
        <code>per_device</code> or <code>once</code>. Defaults to
        <code>once</code> for <code>git</code>, <code>slack_message</code> and
        <code>email_send</code>, and to <code>per_device</code> otherwise. You can
        change to <code>per_pool</code> later from the editor.
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
        else (<code>git</code>, <code>slack_message</code>, <code>email_send</code>).
        For those three the body is documentation only: the handler reads its
        parameters from each workflow node's config.
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
      JSON describing how the engine should retry transient failures. Keys:
      <code>max_retries</code>, <code>initial_delay_seconds</code>,
      <code>backoff</code> (<code>exponential</code>, <code>linear</code> or
      <code>fixed</code>) and <code>max_delay_seconds</code>. An empty object means
      no retries.
    </p>
    <p>
      Only failures that are safe to repeat are retried: a connection that could
      not be opened (SSH, HTTP), a timeout or 5xx on a read request, and a
      <code>429</code> or <code>503</code> on any request. A write that may have
      reached the server, an authentication error or a failure in your own code is
      never retried. All attempts run in the same step, and its log lists every
      failed attempt.
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
      This section is a summary; the full procedure is in
      <a href="/docs/admin/python-packages">Python packages</a>.
    </p>

    <Callout tone="admin" title="Admin-only, sandbox unchanged">
      Only admins manage the list — it relaxes which modules may be imported, so
      the admin is the final barrier. It does <strong>not</strong> lift the rest
      of the sandbox: dangerous built-ins (<code>exec</code>, <code>eval</code>,
      <code>compile</code>, <code>__import__</code>, <code>open()</code>,
      <code>getattr</code>/<code>setattr</code>/<code>delattr</code>,
      <code>vars</code>/<code>globals</code>/<code>locals</code>, <code>input</code>,
      <code>breakpoint</code>, <code>memoryview</code>), interpreter internals and
      relative imports stay blocked, and the snippet still runs with <strong>no network</strong>
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

  <section id="integration-credentials">
    <h2>Integration credentials in Python</h2>
    <p>
      A <code>python_snippet</code> can call a registered
      <a href="/docs/integrations">integration</a> with its stored credentials:
      put <code>&lt;handle&gt;_integration_id</code> (for example
      <code>netbox_integration_id</code>) in the node's config with the
      integration's id or exact name, and the script calls
      <code>integration("netbox")</code>. The runtime hands the script that
      integration's base URL and auth headers.
    </p>
    <Callout tone="warning" title="The snippet must be network-enabled">
      <code>integration()</code> makes its HTTP call from inside the sandbox, and a
      snippet that is not <a href="#network-enabled">network-enabled</a> runs with no
      network at all. On a deployed worker (bubblewrap sandbox) such a step fails
      before the script starts, with a message naming the integrations it declared.
      Either an admin ticks <strong>Network enabled</strong> on the snippet, or the
      HTTP call moves to an <code>integration_action</code> node whose output the
      snippet reads. Local development runs without the sandbox, so the call works
      there either way. Don't take that as proof it will work once deployed.
    </Callout>
    <Callout tone="warning" title="The id must be written in the node">
      Because the script can read those headers, only the workflow's author picks
      the integration: the value must be a literal in the node's own config. An id
      that comes from the run input, a trigger's input defaults or a
      <code>{'{{ … }}'}</code> template fails the step with
      <code>integration_not_authored</code> before any credential is handed out. A
      subflow only passes on the ids written in its subflow node.
    </Callout>
    <p>The check mirrors what the handler itself reads, key for key:</p>
    <ul>
      <li>
        It looks at <strong>one object only</strong> — the payload's
        <code>input</code> object when it has one, the payload itself otherwise,
        never both. A key in the object the handler ignores is ignored here too,
        so the step never fails over a value no script can reach. The matching
        config entry has to sit in the same place: a key under
        <code>input</code> is compared against the config's <code>input</code>
        object, a top-level key against the config's top level.
      </li>
      <li>
        A value counts as authored when the node's config holds the same key with
        the same value as a plain string — byte for byte, and with no
        <code>{'{{'}</code> in it. A subflow child also accepts a key it inherited
        from its parent's subflow node, because that input was already filtered
        by this same rule.
      </li>
      <li>
        Only entries the handler would act on are considered: the value must be a
        non-blank string. A bare <code>_integration_id</code> key, with nothing
        before the underscore, leaves an empty handle that the handler skips, so
        it grants nothing and is not checked.
      </li>
    </ul>
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
