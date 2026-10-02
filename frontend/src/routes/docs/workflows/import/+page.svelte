<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Importing workflows"
  lead="Bring a workflow in from another FlowWeaver instance, from an earlier export of this one, or from n8n, Itential or another DAG tool. The import wizard analyses the file, shows what this instance is missing, and creates the workflow only when you commit."
>
  <Callout tone="where" title="Where to find it">
    <a href="/workflows/import"><code>/workflows/import</code></a>. Sidebar →
    <strong>Build</strong> → <strong>Workflows</strong>, then click
    <strong>Import</strong> in the page header. The button only appears while the
    <strong>Draft</strong> tab is active. It also appears in the empty state when there
    are no draft workflows yet.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      The wizard turns a workflow file into a workflow on this instance. It handles
      three kinds of file:
    </p>
    <ul>
      <li>
        <strong>A FlowWeaver bundle</strong>: the portable export. It carries its
        own snippet definitions and the names of the integrations it uses. Use it to
        share a workflow with another FlowWeaver instance.
      </li>
      <li>
        <strong>A FlowWeaver YAML or JSON export</strong>: nodes that point at
        this instance's GUIDs. Use it only to re-import into the instance that
        wrote it.
      </li>
      <li>
        <strong>A foreign definition</strong>: an n8n workflow, an Itential IAP /
        Operations Manager workflow, or any other DAG-shaped JSON/YAML. It is
        translated, and you decide what to do about each reference this instance
        cannot resolve.
      </li>
    </ul>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>
        You need the <code>workflow.import</code> capability. In the default role
        model that means <strong>Operator</strong> or <strong>Admin</strong>. See
        <a href="#permissions">Permissions and security</a>.
      </li>
      <li>
        Get the file from the source system. On a FlowWeaver instance, open the
        workflow in the editor and click <strong>Export</strong>:
        <ul>
          <li><strong>Bundle (share / portable)</strong> downloads <code>&lt;name&gt;.bundle.json</code>. Use this one for another instance.</li>
          <li><strong>YAML (this instance)</strong> and <strong>JSON (this instance)</strong> reference GUIDs and only resolve on the instance that wrote them.</li>
          <li><strong>Python script</strong> and <strong>Ansible playbook</strong> are one-way renderings. <strong>You can't import them.</strong></li>
        </ul>
      </li>
      <li>
        <strong>For a bundle:</strong> the receiving instance must already have every
        <strong>integration</strong>, <strong>MCP server</strong>,
        <strong>credential</strong> and <strong>git repository</strong> the workflow
        uses, under the same name, with its own secrets. Secrets never travel in a
        bundle. Snippets don't need to exist in advance: the bundle carries their
        definitions.
      </li>
      <li>
        <strong>For a foreign file:</strong> AI drafting of missing snippet bodies,
        and translation of shapes no built-in detector recognises, need a configured
        AI provider (see <a href="/docs/ai/providers">Providers</a>). Everything
        else works without one.
      </li>
      <li>The file must be 5 MiB or smaller.</li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>

    <h3>Accepted files and how the format is detected</h3>
    <p>
      The file picker accepts <code>.yaml</code>, <code>.yml</code> and
      <code>.json</code>. On the server, the content decides the format, not the
      file extension. After an optional UTF-8 byte-order mark and any leading
      whitespace, a body that starts with <code>{'{'}</code> or <code>[</code> is
      parsed as JSON. Anything else is parsed as YAML, and only the first YAML
      document is used.
    </p>
    <table>
      <thead><tr><th>Format</th><th>How it is recognised</th><th>Confidence shown</th></tr></thead>
      <tbody>
        <tr>
          <td>Bundle</td>
          <td>
            A JSON object whose <code>kind</code> is
            <code>flow_weaver.workflow_bundle</code> (some older format identifiers
            are also accepted).
            This check runs <em>before</em> any other detector and ignores the
            format hint. Accepted <code>schema_version</code> values are
            <code>v2</code> and <code>v3</code>. The export writes <code>v3</code>.
          </td>
          <td>100%, reported as the <code>kind</code> value</td>
        </tr>
        <tr>
          <td><code>flow_weaver_v1</code></td>
          <td>
            Top-level <code>nodes</code> and <code>edges</code> arrays and a
            <code>snippet_id</code> on the first node. With a top-level
            <code>schema_version: v1</code> as well, the confidence is 100%. The
            YAML/JSON exports nest that field under <code>workflow:</code>, so they
            are detected at 85%.
          </td>
          <td>100% or 85%</td>
        </tr>
        <tr>
          <td><code>n8n</code></td>
          <td>A non-empty <code>nodes</code> array plus a <code>connections</code> <em>object</em>. 95% when the first node's <code>type</code> starts with <code>n8n-nodes-</code>, otherwise 60%.</td>
          <td>95% or 60%</td>
        </tr>
        <tr>
          <td><code>itential</code></td>
          <td>Non-empty <code>tasks</code> and <code>transitions</code> <em>objects</em>. 95% when the first task has <code>app</code> or <code>type</code>.</td>
          <td>95%, 60% or 50%</td>
        </tr>
        <tr>
          <td><code>generic_dag</code></td>
          <td>
            Anything else shaped like a graph. It is translated by the AI agent.
            <strong>This row never wins auto-detect:</strong> the agent path is
            excluded from the detector ranking, so a file only the AI translator
            can handle is reported as <code>unknown</code>, not as
            <code>generic_dag</code>.
          </td>
          <td>Never shown (see <code>unknown</code> below)</td>
        </tr>
      </tbody>
    </table>
    <p>
      With <strong>Format hint (optional)</strong> left on <strong>Auto-detect</strong>,
      the highest-scoring detector wins. If none matches, the format shows as
      <code>unknown</code> (confidence 0%) and the file goes to the AI translator.
      The other hint values work as follows:
    </p>
    <dl>
      <dt>FlowWeaver v1 (our format) / n8n / Itential IAP / Operations Manager</dt>
      <dd>Forces that translator, whatever the detectors say (confidence 100%).</dd>
      <dt>Smart routing (let the system pick)</dt>
      <dd>
        Uses a built-in translator if a detector scores 85% or more. Otherwise
        the AI translator runs. A translation note explains the choice.
      </dd>
    </dl>

    <h3>Two import paths</h3>
    <table>
      <thead><tr><th></th><th>Bundle</th><th>YAML / JSON export and foreign formats</th></tr></thead>
      <tbody>
        <tr>
          <td>Resolution</td>
          <td>
            Deterministic, by identity. It never guesses. If something can't be
            resolved, the analysis fails and lists every missing item.
          </td>
          <td>By GUID first. Anything unresolved is listed for you to decide.</td>
        </tr>
        <tr>
          <td>Missing snippets</td>
          <td>Created from the definitions the bundle carries.</td>
          <td>You choose: stub, AI-generated, mapped, or skipped.</td>
        </tr>
        <tr>
          <td>Missing integrations</td>
          <td>Fatal. Create the integration on this instance, then import again.</td>
          <td>You choose: create in <code>needs_config</code> status, or map to an existing one.</td>
        </tr>
        <tr>
          <td>Target environment</td>
          <td>Always <code>draft</code>.</td>
          <td>Always <code>draft</code>.</td>
        </tr>
        <tr>
          <td>Triggers</td>
          <td>Created with the workflow, <strong>disabled</strong> and without targets.</td>
          <td>Not imported.</td>
        </tr>
      </tbody>
    </table>

    <h3>How a bundle resolves dependencies</h3>
    <dl>
      <dt>Integrations</dt>
      <dd>
        Matched by slug first, then by exact name (case-insensitive), never by a
        fuzzy match. A name match, or a slug match with a different local name,
        adds a note asking you to confirm it is the same system. Each
        <strong>action</strong> the workflow uses must exist under that integration
        with the same name (case-insensitive).
      </dd>
      <dt>Snippets</dt>
      <dd>
        Reused when a local snippet has the same slug, or failing that the same
        name, <strong>and the same type</strong>. A same-named snippet of a
        different type is left alone and a new one is created (with a note).
        Created snippets keep the source slug when it is free. They are always
        created <strong>unverified</strong> and <strong>without</strong>
        <code>network_enabled</code>, even if the source had it. An admin has to
        turn that flag back on after reviewing the code.
      </dd>
      <dt>MCP servers, credentials, git repositories</dt>
      <dd>Matched by name only. Only their identity travels in the bundle.</dd>
      <dt>Sub-workflows</dt>
      <dd>
        Every sub-workflow reachable through <code>subflow</code> nodes travels in
        the bundle and is created first. An existing active workflow with the same
        name and identical nodes and edges is reused instead of being cloned.
      </dd>
      <dt>Snippet types</dt>
      <dd>A snippet type with no handler on this instance is a missing dependency.</dd>
      <dt>Secrets</dt>
      <dd>
        <code>${'{'}secret:…{'}'}</code> references are kept as written, with a note.
        A plain credential value written inline on an <code>ssh</code> node can't be
        carried over, and the import is refused.
      </dd>
    </dl>

    <h3>How YAML/JSON exports and foreign formats resolve</h3>
    <ul>
      <li>
        A node's <code>snippet_id</code> or <code>config_overrides.integration_id</code>
        that is a GUID of an active local row resolves silently. Any other value
        (a GUID from another instance, or a name produced by a translator) is
        listed under <strong>Missing snippets</strong> or <strong>Missing
        integrations</strong>.
      </li>
      <li>
        Each missing snippet gets an <strong>inferred type</strong> guessed from its
        name. For example, <code>ssh</code>/<code>cli</code>/<code>exec</code> →
        <code>ssh</code>. Product or notification words such as <code>netbox</code>,
        <code>slack</code> or <code>notify</code> → <code>integration_action</code>.
        <code>http</code>/<code>rest</code>/<code>api</code> →
        <code>rest_call</code>. The fallback is <code>python_snippet</code>.
      </li>
      <li>
        Up to five mapping candidates are offered, scored by name similarity
        (Levenshtein, 0–100%). For integration-like names, a keyword match also
        counts: <code>notify</code> suggests integrations whose names contain
        <code>mail</code>, <code>slack</code>, <code>teams</code> and similar
        (score 85%).
      </li>
      <li>
        While the analysis runs, the server tries to draft a body with AI for every
        missing <code>python_snippet</code> or <code>transform</code>. It skips
        entries that already have an integration candidate scoring 85% or more.
        Drafted entries arrive labelled <strong>AI-drafted</strong>.
      </li>
    </ul>

    <h3>Automatic fuzzy matching of integration actions</h3>
    <p>
      This applies only to the YAML/JSON and foreign-format path, at commit time.
      For an <code>integration_action</code> node that names its action
      (<code>action_name</code>) but has no resolved <code>action_id</code>,
      FlowWeaver looks for an action with that exact name under the node's
      integration:
    </p>
    <ul>
      <li>Exactly one match: the node is filled in (<code>action_id</code>, method, path).</li>
      <li>More than one match: the node is left alone and a warning says the name is ambiguous.</li>
      <li>
        No match: every action under that integration is scored against the name.
        The score is 70% name, 25% the last path segment and 5% description. The
        best candidate is applied automatically only if it clears
        <strong>both</strong> thresholds:
        <ul>
          <li><strong>Minimum score</strong> (default <code>0.80</code>, range 0.50–0.99)</li>
          <li><strong>Margin over runner-up</strong> (default <code>0.10</code>, range 0.00–0.50)</li>
        </ul>
        An automatic match still produces a warning so you can check it. Below
        the thresholds, the node is left for you to fix in the editor. The warning
        names the closest candidate when it scores at least 0.40.
      </li>
    </ul>
    <Callout tone="admin" title="Tuning the thresholds">
      Both values are in <a href="/admin/settings"><code>/admin/settings</code></a>,
      in the <strong>Import wizard fuzzy match</strong> card. Only admins can change
      them. Values outside the ranges are clamped when saved.
    </Callout>

    <h3>Limits</h3>
    <table>
      <thead><tr><th>Limit</th><th>Value</th></tr></thead>
      <tbody>
        <tr><td>File size</td><td>5 MiB, checked in the browser and on the server.</td></tr>
        <tr><td>Draft lifetime</td><td>30 minutes from upload. After that the token is gone and you have to upload again.</td></tr>
        <tr><td>Draft visibility</td><td>Only the user who uploaded the file can read or commit it.</td></tr>
        <tr><td>Rewrite hint for AI drafts</td><td>Clipped to 512 characters on the server.</td></tr>
        <tr>
          <td>Structural-duplicate threshold</td>
          <td>
            <strong>Exact match only</strong>: the comparison uses a hash of the
            normalized graph, so it returns either 100% or 0%. The card never appears for a workflow that merely resembles
            the import, and the <strong>Match score</strong> is always 100%.
          </td>
        </tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>The screen and its fields</h2>
    <p>
      The header reads <strong>Import workflow</strong>, with a
      <strong>Back</strong> button. Below it, a step indicator shows
      <code>1. upload › 2. analyzing › 3. review › 4. committed</code>. If the
      analysis fails, the indicator ends in a red <code>failed</code> step.
    </p>

    <h3>Step 1: upload</h3>
    <dl>
      <dt>Workflow file</dt>
      <dd>
        <strong>Choose file</strong> opens the picker. Once a file is chosen, the
        button reads <strong>Choose another file</strong>, and the file name and
        size in bytes appear below it. Choosing a file only reads it in the
        browser.
      </dd>
      <dt>Format hint (optional)</dt>
      <dd>
        <strong>Auto-detect</strong>, <strong>FlowWeaver v1 (our format)</strong>,
        <strong>n8n</strong>, <strong>Itential IAP / Operations Manager</strong>,
        <strong>Smart routing (let the system pick)</strong>.
      </dd>
      <dt>Analyze</dt>
      <dd>Uploads the file and starts the analysis. It stays disabled until a file is chosen.</dd>
      <dt>Cancel</dt>
      <dd>Discards the draft (if there is one) and returns to <code>/workflows</code>.</dd>
    </dl>

    <h3>Step 2: analyzing</h3>
    <p>
      A spinner shows the server's current step, streamed live. See
      <a href="#empty-loading">Empty and loading states</a>.
    </p>

    <h3>Step 3: review</h3>
    <p>Only the cards that apply appear, in this order:</p>
    <dl>
      <dt>Format detected</dt>
      <dd>
        The detected format and its confidence. A
        <strong>Translation notes (n)</strong> link expands the translator's notes.
      </dd>
      <dt>Structural duplicate detected: "&lt;name&gt;"</dt>
      <dd>
        Shown when an existing workflow has <em>exactly</em> the same graph —
        the same step types in the same topology with the same config
        overrides. It includes a summary and a <strong>Match score</strong>,
        which for that reason always reads 100%. Options:
        <strong>Import as new (default)</strong>, <strong>Skip — cancel import</strong>,
        <strong>Update existing workflow</strong>. <em>Update existing</em> keeps
        the existing workflow's id, history and environment. It saves the current
        state as a version, then overwrites nodes and edges and increments the
        version number.
        <Callout tone="warning" title="Skip and Commit import">
          Of these options the server only acts on <strong>Update existing
          workflow</strong>. With <strong>Skip — cancel import</strong>,
          <strong>Commit import</strong> creates a new workflow, the same as
          <strong>Import as new</strong>. To abandon an import, click
          <strong>Cancel</strong>.
        </Callout>
      </dd>
      <dt>A workflow with the same name exists in &lt;env&gt; (v&lt;n&gt;)</dt>
      <dd>
        A name collision. The YAML/JSON and foreign-format path always looks for
        it in <code>draft</code>; the bundle path looks in the environment the
        bundle declares for its workflow, falling back to <code>draft</code> when
        it declares none — so a bundle exported from production reports the
        collision it finds in <code>production</code>, even though the import
        itself still lands in draft. The <strong>Resolution</strong> options are:
        <ul>
          <li><strong>Rename the import</strong>: preselected when a collision is found, with <strong>New name</strong> prefilled as <code>&lt;name&gt; (imported)</code>. If the name is left empty, it becomes <code>&lt;name&gt; (copy yyyyMMdd-HHmmss)</code>.</li>
          <li><strong>Import as fresh copy with timestamped name</strong>: <code>&lt;name&gt; (copy yyyyMMdd-HHmmss)</code>.</li>
          <li><strong>Replace the existing workflow (requires owner grant)</strong>: deactivates the existing workflow and creates the import alongside it.</li>
          <li><strong>Keep existing — cancel this import</strong>: nothing is created. The result says "Import cancelled — kept existing workflow."</li>
        </ul>
        For a bundle, only <strong>Keep existing — cancel this import</strong> is
        applied. With any other choice, and whatever the duplicate card says, the
        bundle is imported as a new workflow under its own name.
      </dd>
      <dt>Rollback risk</dt>
      <dd>
        Lists the <strong>non-reversible step(s)</strong> (red) and the number of
        steps that <strong>need a compensation edge</strong> (amber). It is for
        information only and does not block the import.
      </dd>
      <dt>Missing snippets (n)</dt>
      <dd>
        One card per reference, showing the imported id and <strong>Inferred type</strong>.
        The <strong>Action</strong> options are:
        <ul>
          <li><strong>Create empty stub (default)</strong>: a placeholder snippet. For an inferred <code>integration_action</code>, an action with that name is created instead, under a shared integration called <code>imported_actions</code> (status <code>needs_config</code>).</li>
          <li><strong>Generate with AI</strong>: shows <strong>Draft body with AI</strong>. Once a draft exists, <strong>Preview</strong> and <strong>Regenerate</strong> appear.</li>
          <li><strong>Map to existing snippet</strong>: only offered when there are candidates. Pick one under <strong>Target snippet</strong>, or under <strong>Target (snippet or integration)</strong> for an inferred <code>integration_action</code>. Each candidate shows its similarity. Mapping to an integration creates or reuses an action with the imported id as its name under that integration.</li>
          <li><strong>Skip — drop nodes that use it</strong>: removes every node that references it, together with their edges.</li>
        </ul>
      </dd>
      <dt>Missing integrations (n)</dt>
      <dd>
        One card per reference, showing the imported id and, when known, the
        <strong>Inferred base URL</strong>. The <strong>Action</strong> options are
        <strong>Create with needs_config status</strong> (default) and
        <strong>Map to existing integration</strong> (only when there are
        candidates), which reveals <strong>Target integration</strong>. A new
        integration is created as <code>generic_rest</code> with no credentials.
        FlowWeaver also creates one action for each action name the workflow uses
        under it, with the HTTP method guessed from the name
        (<code>get</code>/<code>list</code> → GET, <code>create</code>/<code>send</code>
        → POST, <code>update</code>/<code>set</code> → PATCH,
        <code>replace</code>/<code>put</code> → PUT, <code>delete</code>/<code>remove</code>
        → DELETE, anything else → GET) and an empty path.
      </dd>
      <dt>Landing environment</dt>
      <dd>
        A note, not a choice: every import lands in <strong>Draft</strong>. To reach
        QA or production, <a href="/docs/workflows">promote</a> the workflow
        afterwards, through the QA gate and the second approver. The API refuses any
        other <code>target_environment</code> with
        <code>import_target_environment_not_allowed</code>.
      </dd>
      <dt>Commit import / Cancel</dt>
      <dd>Commit creates the workflow. Cancel discards the draft.</dd>
    </dl>

    <h3>AI-drafted snippet dialog</h3>
    <p>
      Opens from <strong>Preview</strong> (or automatically after a generation).
      It shows the draft's <strong>Name</strong>, <strong>Type</strong>,
      <strong>Script language</strong>, <strong>Target mode</strong>,
      <strong>Description</strong>, <strong>Code</strong>, <strong>Input schema</strong>
      and <strong>Output schema</strong>. The optional
      <strong>Tell the agent what to change</strong> box changes the middle button
      from <strong>Regenerate</strong> to <strong>Rewrite with hint</strong>.
      The footer buttons are:
    </p>
    <ul>
      <li><strong>Discard draft</strong>: falls back to an empty stub.</li>
      <li><strong>Regenerate</strong> / <strong>Rewrite with hint</strong>: asks the agent for a new draft.</li>
      <li><strong>Looks good</strong>: closes the dialog and keeps the draft.</li>
    </ul>

    <h3>Step 4: committed</h3>
    <p>
      The screen shows <strong>Workflow imported</strong> and an
      <strong>Open workflow</strong> button. Each warning from the server appears
      as a separate <strong>Heads up</strong> toast.
    </p>
  </section>

  <section>
    <h2>Main procedure</h2>
    <ol>
      <li>Go to <strong>Workflows</strong> (Draft tab) and click <strong>Import</strong>.</li>
      <li>Click <strong>Choose file</strong> and pick the <code>.bundle.json</code>, <code>.yaml</code>, <code>.yml</code> or <code>.json</code> file.</li>
      <li>Leave <strong>Format hint</strong> on <strong>Auto-detect</strong>, unless a previous attempt picked the wrong format.</li>
      <li>Click <strong>Analyze</strong> and wait for the review step.</li>
      <li>Check <strong>Format detected</strong>. If it is wrong, click <strong>Cancel</strong> and upload again with a hint.</li>
      <li>Resolve any duplicate or name-collision card.</li>
      <li>
        For each missing snippet, choose an action. Prefer <strong>Map to existing
        snippet</strong> when a good candidate exists. For AI drafts, open
        <strong>Preview</strong> and read the code before accepting it.
      </li>
      <li>For each missing integration, map it to the real system if it exists here. Otherwise let the wizard create it in <code>needs_config</code> status.</li>
      <li>Click <strong>Commit import</strong>. Read every <strong>Heads up</strong> toast, then click <strong>Open workflow</strong>.</li>
      <li>
        Finish the setup the warnings asked for. That can mean adding credentials
        to <code>needs_config</code> integrations under
        <a href="/integrations"><code>/integrations</code></a>, setting paths on
        auto-created actions, writing stub snippets, checking fuzzy-matched nodes
        in the editor, or configuring and enabling imported triggers. Then test in
        draft and promote as usual.
      </li>
    </ol>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Setup:</strong> two instances, <em>A</em> (the source) and
      <em>B</em> (the destination). You have an Operator or Admin account on both.
      <em>A</em> has a workflow <code>Interface audit</code> that runs a
      <code>ping</code> snippet and then calls the <code>get_interfaces</code>
      action of an integration named <code>NetBox</code>. <em>B</em> already has
      an integration named <code>NetBox</code> with an action called
      <code>get_interfaces</code>, and its own credentials.
    </p>

    <h3>Case 1: a clean import</h3>
    <ol>
      <li>On <em>A</em>, open <code>Interface audit</code> → <strong>Export</strong> → <strong>Bundle (share / portable)</strong>. You get <code>Interface_audit.bundle.json</code>.</li>
      <li>On <em>B</em>, open <code>/workflows/import</code>, choose that file and click <strong>Analyze</strong>.</li>
      <li>
        The progress messages run: <em>Parsing upload... → Reading bundle... →
        Resolving against this instance... → Detecting conflicts... →
        Analyzing rollback risk...</em>
      </li>
      <li>
        The review shows <strong>Format detected</strong>
        <code>flow_weaver.workflow_bundle (confidence 100%)</code> and no missing
        dependency cards.
      </li>
      <li>Click <strong>Commit import</strong>.</li>
    </ol>
    <p>
      <strong>Result:</strong> <code>Interface audit</code> is created in
      <code>draft</code> on <em>B</em>. <code>NetBox</code> is matched and the
      <code>ping</code> snippet is reused if <em>B</em> already has it, or created
      from the bundle if not. Toasts show any notes. For example, if <em>B</em>'s
      integration was matched by name rather than by slug, a note asks you to
      confirm it is the same system.
    </p>

    <h3>Case 2: a dependency that can't be resolved automatically</h3>
    <p>
      Repeat the import on a third instance, <em>C</em>, which has no
      <code>NetBox</code> integration. The analysis stops with
      <strong>Import analysis failed</strong> and a message like this:
    </p>
    <pre><code>{`Pipeline failed: this instance is missing dependencies the workflow needs:
integration 'NetBox' (slug 'netbox', type 'generic_rest') — source base_url
https://netbox.example.com. Create them here (with their own credentials)
and import again.`}</code></pre>
    <p>
      <strong>Recovery:</strong> on <em>C</em>, create an integration named
      <code>NetBox</code> in <a href="/integrations"><code>/integrations</code></a>
      with <em>C</em>'s own URL and credentials, and make sure it has an action
      named <code>get_interfaces</code>. Back in the wizard, click
      <strong>Try again</strong>, choose the same file and analyze it again. The
      import now goes through as in Case 1. A bundle never creates a placeholder
      integration: an unresolved integration is always an error you fix on the
      destination instance.
    </p>
    <Callout tone="info" title="If you had exported YAML instead">
      Importing the <strong>YAML (this instance)</strong> export on <em>B</em>
      shows the same references as <strong>Missing snippets</strong> and
      <strong>Missing integrations</strong>, because <em>A</em>'s GUIDs mean
      nothing on <em>B</em>. You could map them to <em>B</em>'s rows by hand, but
      the bundle does that for you. Use the bundle between instances.
    </Callout>
  </section>

  <section id="permissions">
    <h2>Permissions and security</h2>
    <ul>
      <li>
        Every import endpoint requires <code>workflow.import</code>. In the default
        role model this is <strong>Operator</strong> or <strong>Admin</strong>.
        Without it, the server refuses the upload.
      </li>
      <li>
        When per-resource grants are enabled, <strong>Replace the existing
        workflow</strong> needs an <strong>owner</strong> grant on the target (it
        removes it, like a delete; otherwise <code>missing_owner_grant</code>), and
        <strong>Update existing workflow</strong> needs an <strong>editor</strong>
        grant (otherwise <code>missing_editor_grant</code>).
      </li>
      <li>
        Neither can touch a <strong>production</strong> workflow: the server answers
        <code>production_immutable</code>, the same as a direct edit or delete. Clone
        it to draft instead.
      </li>
      <li>
        Every commit runs the same <a href="/docs/policies">policy</a> check as
        creating or editing a workflow by hand: <code>create</code> in
        <code>draft</code> for a new workflow, <code>update</code> in the target's
        environment for <strong>Update existing</strong>. A matching deny rule
        answers <code>policy_blocked</code> and nothing is saved.
      </li>
      <li>
        The same save-time checks as the editor apply to the imported graph: a
        <code>git</code> step that writes needs <code>git.manage</code>, and a node
        whose config contains a <code>${'{'}secret:…{'}'}</code> reference needs
        <code>secret.read</code>.
      </li>
      <li>Only the user who uploaded a draft can see, commit or discard it.</li>
      <li>
        <strong>Credentials are never imported.</strong> A bundle describes an
        integration only by slug, name, type and base URL. Its auth config,
        headers and credentials stay on the source instance, and so does a webhook
        trigger's signing secret. Integrations created by the wizard start in
        <code>needs_config</code>, and runs that use them are refused until an
        admin adds credentials.
      </li>
      <li>Imported snippets are unverified, and <code>network_enabled</code> is always dropped.</li>
      <li>
        Imported triggers are created <strong>disabled</strong>, without target
        devices, and with a <strong>new</strong> signing secret. They always
        require signatures, even if the source accepted unsigned deliveries. To
        use one, rotate its secret, set its targets and enable it. See
        <a href="/docs/workflows/triggers">Triggers and webhooks</a>.
      </li>
      <li>
        Bundles are created through the normal workflow-create path, so schema,
        reference and <a href="/docs/policies">policy</a> checks apply.
      </li>
      <li>
        Upload and commit share the per-user write budget (60 per minute). AI
        snippet generation uses the AI budget (30 per hour).
      </li>
      <li>Every step (upload, commit, replace, created integrations) is written to the audit log.</li>
    </ul>
  </section>

  <section id="empty-loading">
    <h2>Empty and loading states</h2>
    <ul>
      <li><strong>No file chosen:</strong> <strong>Analyze</strong> is disabled.</li>
      <li>
        <strong>Analyzing:</strong> the spinner shows the current step. Before the
        first message it reads <em>Starting analysis…</em>. For foreign files the
        messages are <em>Parsing upload...</em>, <em>Detecting format...</em>,
        <em>Translating from &lt;format&gt;...</em>, <em>Resolving
        dependencies...</em>, <em>Pre-drafting snippet bodies (AI)...</em>,
        <em>Detecting conflicts...</em> and <em>Analyzing rollback risk...</em>.
        AI steps can take a while.
      </li>
      <li>
        <strong>Connection dropped:</strong> the analysis continues on the server.
        The page checks the status once. If the result isn't ready, it shows
        <em>Connection to the server was lost. Refresh the page or retry.</em>
      </li>
      <li>
        <strong>Nothing missing:</strong> the missing-snippet and
        missing-integration cards are simply absent. This is always the case for
        a bundle.
      </li>
      <li>
        <strong>Leaving the page</strong> before committing discards the draft on
        the server and cancels any AI calls still running.
      </li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>What you see</th><th>Cause</th><th>What to do</th></tr></thead>
      <tbody>
        <tr><td>Toast <em>File too large — Max 5 MiB per import.</em></td><td>The file is over 5 MiB.</td><td>Split the workflow or trim the file.</td></tr>
        <tr><td><em>Analyze failed</em> toast with <em>empty upload</em></td><td>The file is 0 bytes.</td><td>Choose the right file.</td></tr>
        <tr><td><em>Import analysis failed</em> — <em>Pipeline failed: …</em></td><td>The file couldn't be parsed, translation failed, or a bundle was refused. The server's message follows the prefix.</td><td>Read the message, fix the cause, then click <strong>Try again</strong>.</td></tr>
        <tr><td>… <em>schema_version '…' is not supported by this instance</em></td><td>The bundle is newer or older than this instance understands.</td><td>Upgrade FlowWeaver, or re-export from the source.</td></tr>
        <tr><td>… <em>is not a workflow bundle marker this instance reads</em></td><td>An unknown <code>kind</code>.</td><td>Re-export it as a FlowWeaver bundle.</td></tr>
        <tr><td>… <em>this instance is missing dependencies the workflow needs: …</em></td><td>An integration, action, MCP server, credential, git repository or snippet type is missing here.</td><td>Create each listed item on this instance, with its own credentials, and import again.</td></tr>
        <tr><td>… <em>the bundle does not carry everything its own graph references</em></td><td>The bundle is missing a sub-workflow it uses. The fault is in the export, not on this instance.</td><td>Re-export from the source instance.</td></tr>
        <tr><td>… <em>the bundle references things this instance cannot translate</em></td><td>A node names something that doesn't exist here, or has a plain inline credential.</td><td>Create the item under the same name, or fix the node at the source, then re-export.</td></tr>
        <tr><td>… <em>the bundle requires capabilities this instance does not implement</em></td><td>The bundle relies on a feature this build doesn't have.</td><td>Upgrade, or remove that feature from the workflow at the source.</td></tr>
        <tr><td><em>Commit failed</em> — <em>no resolution provided for missing snippet/integration '…'</em></td><td>A missing item has no action selected.</td><td>Choose an action on every card.</td></tr>
        <tr><td><em>Commit failed</em> — <em>action=map for '…' requires target_id</em></td><td><strong>Map</strong> was chosen but no target was picked.</td><td>Pick a target, or choose another action.</td></tr>
        <tr><td><em>Commit failed</em> — <em>imported workflow fails v1 schema validation after rewriting</em> (with up to 5 details)</td><td>After your choices, the graph is not a valid workflow.</td><td>Read the details. Skipped nodes often leave a gap in the graph. Change your choices, or fix the file.</td></tr>
        <tr><td><em>Commit failed</em> — <em>imported workflow references resources that do not exist or are not active</em></td><td>A mapped target is missing or inactive.</td><td>Map to an active snippet or integration. Nothing was saved, so you can retry straight away.</td></tr>
        <tr><td><em>missing_owner_grant</em> / <em>missing_editor_grant</em></td><td>You chose Replace without an owner grant, or Update existing without an editor grant, on that workflow.</td><td>Ask an admin for the grant, or choose Rename.</td></tr>
        <tr><td><em>production_immutable</em></td><td>Replace or Update existing targeted a production workflow.</td><td>Import as a new workflow, or clone the production workflow to draft and work there.</td></tr>
        <tr><td><em>policy_blocked</em></td><td>A policy denies creating (or updating) this workflow.</td><td>Read the policy's reason; change the workflow or ask an admin about the rule.</td></tr>
        <tr><td><em>import_target_environment_not_allowed</em></td><td>A client asked for an environment other than draft.</td><td>Import to draft and promote.</td></tr>
        <tr><td><em>draft not ready</em></td><td>You tried to commit before the analysis finished.</td><td>Wait for the review step.</td></tr>
        <tr><td><em>import draft … not found</em></td><td>The draft is older than 30 minutes, or was discarded.</td><td>Start again from the upload step.</td></tr>
        <tr><td><em>Generation failed</em></td><td>No AI provider is configured, or the AI call failed.</td><td>Configure a provider, or use a stub or a mapping instead.</td></tr>
        <tr><td>Toast <em>rate_limited</em></td><td>Too many uploads or commits in a minute.</td><td>Wait 60 seconds.</td></tr>
      </tbody>
    </table>
    <p>Warnings after a successful commit are for follow-up. They don't mean the import failed:</p>
    <ul>
      <li><em>N integration(s) created in needs_config status. Configure credentials before running.</em></li>
      <li><em>N integration action(s) auto-created on the new integration(s). Open /integrations/&lt;id&gt; to set the path, headers, and request body schema before running the workflow.</em></li>
      <li><em>node '…': action '…' fuzzy-matched to '…' (score 0.87). Review the node in the editor.</em></li>
      <li><em>node '…': action '…' not found under integration '…'. Closest match: '…' (score 0.55). Open the node in the editor and pick an action from the dropdown.</em></li>
      <li><em>Dropped node '…' and its edges because snippet '…' was marked skip.</em></li>
      <li>For bundles, the notes described above, for example <em>trigger '…' (webhook) was created disabled and without targets — …</em></li>
    </ul>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>Python and Ansible exports can't be imported.</li>
      <li>
        YAML/JSON exports only resolve on the instance that wrote them. Also,
        because they keep the workflow name under <code>workflow:</code>, the
        name-collision card doesn't appear for them. The structural-duplicate card
        does.
      </li>
      <li>
        For a bundle, the wizard's review doesn't show which snippets will be
        created or reused. That information arrives as notes after the commit.
      </li>
      <li>
        Mapping candidates are based on names, not endpoints, so two unrelated
        systems can score well. Check low percentages by hand.
      </li>
      <li>
        Drafts live in the server's memory. A restart loses them, and a deployment
        with several API instances needs sticky sessions.
      </li>
      <li>Missing vendor-command catalogs are detected but not shown in the wizard.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a>: editor, export menu, promotion.</li>
      <li><a href="/docs/workflows/triggers">Triggers and webhooks</a>: finishing imported triggers.</li>
      <li><a href="/docs/snippets">Snippets</a>: completing stubs and verifying AI drafts.</li>
      <li><a href="/docs/integrations">Integrations</a>: adding credentials to <code>needs_config</code> integrations.</li>
      <li><a href="/docs/policies">Policies</a>: guardrails applied to created workflows.</li>
      <li><a href="/docs/ai/providers">AI providers</a>: needed for AI translation and drafting.</li>
    </ul>
  </section>
</DocLayout>
