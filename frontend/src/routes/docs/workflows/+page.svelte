<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Workflows"
  lead="Author, version, and ship automation pipelines. The workflow surface has two parts: a list with lifecycle controls, and a visual DAG editor where you compose steps."
>
  <Callout tone="where" title="Where to find it">
    List: <a href="/workflows"><code>/workflows</code></a> ·
    Editor: <code>/workflows/{'{id}'}</code>. Sidebar → <strong>Build</strong> → <strong>Workflows</strong>.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A workflow is a directed acyclic graph (DAG) of <strong>nodes</strong> (calls
      to snippets or integration actions) connected by typed <strong>edges</strong>
      (<em>success</em>, <em>failure</em>, <em>always</em>, or <em>conditional</em>). Every workflow
      lives in one of three environments, each a more constrained copy of the
      previous one:
    </p>
    <ul>
      <li><strong>draft</strong> — editable, safe to iterate, the default when you click <em>New workflow</em>. Runs reach only devices and pools that allow <code>draft</code> (new devices do by default).</li>
      <li><strong>qa</strong> — promoted from draft. Runs reach only devices and pools that allow <code>qa</code> (off by default; see <a href="/docs/qa-lab">QA lab</a>).</li>
      <li><strong>production</strong> — promoted from qa. Runs reach only devices and pools that allow <code>production</code>. New changes require clone→edit→promote.</li>
    </ul>
    <p>
      Promoting copies the workflow into the next environment. The copy appears
      in that environment's tab and the source stays where it was. The qa and
      production copies are locked (padlock icon).
    </p>
    <p>
      Every workflow carries an <strong>input schema</strong> (JSON Schema for the
      <code>input</code> map you can pass at run time), a set of <strong>nodes</strong>
      with per-node <code>config_overrides</code>, and <strong>edges</strong> with
      explicit handle ids so the layout survives a reload.
    </p>
  </section>

  <section>
    <h2>The list page</h2>
    <p>
      Opens at <code>/workflows</code>. A header with title and description, the
      <em>Import</em> and <em>New workflow</em> buttons (only while the Draft tab
      is active), and below it three environment tabs and a search box.
    </p>

    <h3>Tabs</h3>
    <dl>
      <dt>Draft</dt>
      <dd>Work-in-progress workflows. Rows show <em>Promote</em> (to qa) and <em>Edit</em>.</dd>
      <dt>QA</dt>
      <dd>Workflows promoted to qa. Rows show a padlock, <em>Promote</em> (to production), and <em>Clone</em>.</dd>
      <dt>Production</dt>
      <dd>Locked workflows currently serving traffic. Rows show a padlock, a <em>Rollback</em> action, and <em>Clone</em>.</dd>
    </dl>
    <p>
      Switching tabs resets the pagination to offset 0 and refetches with the
      environment filter. The backend also filters by the search keyword when one
      is present (250 ms debounce).
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Name</td><td>Workflow display name. Click it to open the editor. The padlock icon appears on qa and production rows.</td></tr>
        <tr><td>Description</td><td>Free-text description. Truncated to one line; full width on hover.</td></tr>
        <tr><td>Version</td><td>QA and Production tabs only. A green <code>v{'{n}'}</code> badge showing the current version.</td></tr>
        <tr><td>Nodes</td><td>Count of nodes in the DAG (sentinels included).</td></tr>
        <tr><td>Edges</td><td>Count of edges.</td></tr>
        <tr><td>Created</td><td>Creation date (short format).</td></tr>
        <tr><td>Actions</td><td>Row actions — see below.</td></tr>
      </tbody>
    </table>

    <h3>Row actions</h3>
    <dl>
      <dt>Promote (Draft and QA tabs)</dt>
      <dd>
        Opens the <strong>Promote dialog</strong>. Infers the next environment
        (<code>draft → qa</code>, <code>qa → production</code>). Production rows
        don't show this action.
      </dd>
      <dt>Edit (Draft tab)</dt>
      <dd>
        Pencil icon button. Opens a small dialog to rename the workflow and edit
        its description without opening the editor. Draft-only — production
        workflows are immutable (the API rejects edits with
        <code>production_immutable</code>).
      </dd>
      <dt>Rollback (Production tab)</dt>
      <dd>Opens the <strong>Rollback dialog</strong> with the version history.</dd>
      <dt>Clone (QA and Production tabs)</dt>
      <dd>Clones the workflow back into a new draft entry and switches the active tab to Draft.</dd>
      <dt>Delete</dt>
      <dd>Red trash-can icon button. Confirms, then deletes the row.</dd>
    </dl>

    <h3>Creating a workflow</h3>
    <p>
      From the Draft tab, click <em>New workflow</em>. An inline card opens with
      two fields:
    </p>
    <dl>
      <dt>Name</dt>
      <dd>Required. Any string. Duplicates are allowed — the id is the real identifier.</dd>
      <dt>Description</dt>
      <dd>Optional one-line description shown in the list column.</dd>
    </dl>
    <p>
      Submitting returns to the list and refreshes page 1. The new workflow is
      empty until you open it in the editor and start adding nodes.
    </p>

    <h3>Pagination</h3>
    <p>
      The list uses an offset-based pager at 50 rows per page. The counter at the
      bottom reads <em>"Showing N–M of T workflow(s)"</em> with
      <strong>Prev</strong> / <strong>Next</strong> buttons. If a page ends up
      empty because rows have been deleted, the UI silently retries against the
      last non-empty offset so you don't end up on a blank screen.
    </p>
  </section>

  <section>
    <h2>Promote dialog</h2>
    <p>
      Modal opened from a Draft or QA row's <em>Promote</em> button. Its purpose is to
      document what changed between the two environments and, for production,
      capture a second approver.
    </p>

    <h3>What you see</h3>
    <ul>
      <li>The workflow name and an environment pill (<code>draft</code> →
        <code>qa</code>, or <code>qa</code> → <code>production</code>).</li>
      <li>
        A <strong>Changes detected</strong> card listing node and edge diffs:
        <code>+ nodes added</code>, <code>- nodes removed</code>,
        <code>~ nodes changed</code>, and the same for edges. The card shows
        <em>No changes from current {'{env}'} version</em> when the two graphs
        match, and an info alert if this is a first promotion with no existing
        target version.
      </li>
      <li>A <strong>Change summary</strong> textarea — required. Empty input
        disables the submit button.</li>
      <li>
        <strong>Approved by</strong> — production promotions only, required. Name
        the second reviewer. The server rejects the promotion when this name is
        your own username (compared ignoring case and surrounding spaces). The
        value is recorded in the version history and the audit log.
      </li>
    </ul>

    <Callout tone="warning" title="Promotion gates">
      <code>draft → qa</code> needs a successful <em>Simulate</em> of the current
      graph; any later structural edit means you have to simulate again.
      <code>qa → production</code> additionally needs a completed qa run of the
      same workflow within the last 48 hours. That rule is the default policy
      <code>default.qa_to_production</code>, editable under
      <a href="/docs/policies">Policies</a>. The <a href="/qa">QA lab</a> page
      tells you whether each workflow is currently ready.
    </Callout>

    <h3>Behaviour</h3>
    <ul>
      <li>Cancel closes the dialog without side effects.</li>
      <li>Promote creates the copy in the target environment and refreshes the list. The source keeps its tab and its version number moves up by one.</li>
      <li>A gate or policy that blocks the promotion shows its reason as an error; nothing is copied.</li>
    </ul>
  </section>

  <section>
    <h2>Rollback dialog</h2>
    <p>
      Opens from a Production row's <em>Rollback</em> button. Loads the full
      version history of the workflow and lets you restore any older version.
    </p>

    <h3>What you see</h3>
    <ul>
      <li>Workflow name with the <em>current</em> version badge.</li>
      <li>A scrollable list of cards, one per saved version. Each card shows:
        <ul>
          <li>The <code>v{'{n}'}</code> badge.</li>
          <li>The date it was created.</li>
          <li>The change summary entered at promotion time.</li>
          <li>Who promoted it.</li>
          <li>A <em>Restore</em> button, disabled and labeled <em>Current</em> for the active version.</li>
        </ul>
      </li>
    </ul>

    <h3>Behaviour</h3>
    <p>
      Clicking <em>Restore</em> prompts once for confirmation, then creates a new
      <strong>draft</strong> workflow from the chosen version's graph (change
      summary <em>Rolled back to version N</em>). The production workflow is not
      modified: promote the new draft through qa. The rollback is recorded in the
      audit log.
    </p>
    <p>
      Rollback is refused when the chosen version contains non-reversible steps;
      the error lists them. Build a forward fix in a new draft instead.
    </p>
  </section>

  <section>
    <h2>The workflow editor</h2>
    <p>
      The heart of FlowWeaver. Open any workflow from the list to reach it. It
      is organized into three regions: a <strong>palette</strong> on the left, a
      <strong>canvas</strong> in the middle, and a <strong>toolbar</strong> across
      the top.
    </p>

    <h3>Palette (left, 256 px)</h3>
    <p>
      Four groups of draggable items, top to bottom: <em>Composition</em>,
      <em>Snippets</em>, <em>Integrations</em> and <em>MCP tools</em>.
    </p>
    <dl>
      <dt>Composition</dt>
      <dd>
        A single <strong>Subflow</strong> card — "Call another workflow as a
        single step". Drop it and pick the workflow in the node dialog. See
        <a href="/docs/subflows">Subflows</a>.
      </dd>
      <dt>Snippets</dt>
      <dd>
        <p>
          A search box (matches name and type) and a <em>+ New</em> link that
          opens <code>/snippets/new</code>. Below them, the snippets that have
          <strong>completed at least one run</strong>. Each card shows the name, a small badge with the <code>type</code>, and the
          <code>target_mode</code> below. Drag onto the canvas to drop a new node.
        </p>
        <p>
          Everything else lands in a collapsed <strong>Unproven</strong> section
          at the bottom, with a dashed border and a count. That is the whole
          point: half-finished experiments and attempts that never worked pile up
          over time and drown the blocks you can actually reuse.
        </p>
        <p>
          They are moved, never hidden — a new snippet has zero runs, and the
          only way it can ever earn one is by being dragged from this palette
          into a workflow. Drag it in from <em>Unproven</em>, run it, and it
          moves up on the next load. Searching auto-expands the section so a
          name search never appears to come up empty.
        </p>
        <p>
          "Completed" means a <code>step_run</code> reached
          <code>completed</code> — a snippet that ran and only ever failed stays
          unproven, which is the distinction that makes the list worth trusting.
          Deleting old runs does not demote a snippet.
        </p>
      </dd>
      <dt>Integrations</dt>
      <dd>
        Grouped tree: integration → category → action. Expand an integration to
        see its categories, expand a category to see the actions. Action cards
        show an HTTP method pill (colour-coded per method) and the path.
        Draggable the same way as snippets.
      </dd>
      <dt>MCP tools</dt>
      <dd>
        Only shown when at least one <a href="/docs/admin/mcp-servers">MCP
        server</a> has synced tools. Grouped by server; each card is a tool name
        (hover for its description). Dropping one creates an
        <code>mcp_call</code> node with the server and tool already chosen — fill
        in the arguments in the node dialog. The seeded <code>mcp_call</code>
        snippet is deliberately left out of the <em>Snippets</em> group, because
        it is useless without a server and tool.
      </dd>
    </dl>
    <p>
      The palette can be hidden with the <kbd>×</kbd> button in its header; a
      small <em>Show palette</em> link appears next to the breadcrumb when
      collapsed.
    </p>

    <h3>Canvas (center)</h3>
    <p>
      The graph area. Key interactions:
    </p>
    <ul>
      <li><strong>Drag from palette</strong> — drops a new node under the cursor. The drop position is computed in graph coordinates, not screen coordinates, so zoom and pan are honoured.</li>
      <li><strong>Click a node</strong> — opens the node dialog (see below). Sentinel <em>Start</em> / <em>End</em> nodes open the same dialog in read-only mode.</li>
      <li><strong>Drag between handles</strong> — creates a new edge. The default edge type is <code>success</code> (green, animated).</li>
      <li><strong>Right-click an edge</strong> — opens a context menu to switch the edge type or delete it.</li>
      <li><strong>Select + Delete</strong> — removes nodes and connected edges. The <em>Start</em> and <em>End</em> sentinels cannot be deleted.</li>
      <li><strong>Mouse wheel</strong> — zoom. <strong>Drag empty canvas</strong> — pan.</li>
      <li><strong>Controls widget</strong> (bottom-left) — buttons for zoom in/out, fit view, interaction lock.</li>
    </ul>

    <Callout tone="info" title="Auto-layout on first open">
      When a workflow arrives with every node stacked on the same coordinates
      (common for AI-generated DAGs), the editor silently runs an automatic left-to-right
      layout before the first paint. Positions are not saved unless you click
      <em>Auto-layout → Save</em>.
    </Callout>

    <h3>Edge types and colours</h3>
    <p>
      The legend at the bottom centre of the canvas summarises this:
    </p>
    <dl>
      <dt>success</dt>
      <dd>Green, animated. Taken when the source step succeeded.</dd>
      <dt>failure</dt>
      <dd>Red, labelled <em>failure</em>. Taken when the source step failed.</dd>
      <dt>always</dt>
      <dd>Purple. Taken regardless of outcome.</dd>
      <dt>conditional</dt>
      <dd>Amber, labelled with the condition expression. Taken when the condition evaluates true against the source step's output.</dd>
    </dl>
    <p>
      Right-click any edge to flip its type, set a conditional expression, or delete it.
    </p>
  </section>

  <section>
    <h2>Editor toolbar</h2>
    <p>
      Horizontal bar across the top. Left side: breadcrumb back to
      <em>Workflows</em> and the current workflow name. Right side: status
      indicators plus actions.
    </p>

    <h3>Status indicators</h3>
    <dl>
      <dt>Save status</dt>
      <dd>Appears briefly as <em>Saving…</em>, <em>Saved</em>, or <em>Save failed</em> (with the error as tooltip) after each save.</dd>
      <dt>Run status</dt>
      <dd>After a <em>Run</em>, shows <em>Starting…</em> then a clickable <em>Run: {'{id8}'}…</em> link that opens the run detail. Shows <em>Run failed</em> on errors.</dd>
    </dl>

    <h3>Actions (in order, left to right)</h3>
    <dl>
      <dt>Show run data</dt>
      <dd>
        Opens the <strong>Last run data</strong> dialog (see below). Pulls the
        most recent run for this workflow and all its steps into a single
        scrollable modal so you can inspect payloads without leaving the editor.
      </dd>
      <dt>Ask AI</dt>
      <dd>
        Navigates to <code>/ai/chat?context=workflow:{'{id}'}</code>. The assistant
        picks up the workflow context on entry and can inspect or edit it via
        the <code>get_workflow_details</code> and
        <code>update_workflow_node_config</code> tools.
      </dd>
      <dt>Auto-layout</dt>
      <dd>
        Re-runs the automatic left-to-right layout on the in-memory graph and shows a
        toast. Your positions are <strong>not</strong> saved until you click
        <em>Save</em>.
      </dd>
      <dt>Simulate</dt>
      <dd>
        Static validation — no handler runs. Opens the <strong>Workflow simulation</strong>
        dialog with a <em>Ready to run</em> / <em>Needs attention</em> banner,
        lists of <code>issues</code>, <code>warnings</code>, and unchecked
        <em>Not covered</em> notes. Use before running against live devices.
      </dd>
      <dt>Save</dt>
      <dd>
        Validates the graph (at least one real node, Start connected to the
        first real node, last real node connected to End) then posts node
        positions, handles, snippet ids, and config overrides. Sentinel positions
        are saved too so a reopened workflow restores exactly.
      </dd>
      <dt>Export</dt>
      <dd>
        <p>
          Drop-down backed by <code>/workflow/{'{id}'}/export?format=…</code>.
        </p>
        <p>
          <strong>Bundle (share / portable)</strong> is the one to use when handing a
          workflow to <em>another FlowWeaver instance</em>. It carries each referenced
          snippet's full definition plus a stable identity (slug) for every integration
          and action, so the far side binds the workflow to its own copies of those.
          Credentials are never included — the receiving instance uses its own.
        </p>
        <p>
          <strong>YAML</strong> and <strong>JSON</strong> are marked <em>(this instance)</em>
          because that is their limit: they reference snippets and integrations by GUID,
          and GUIDs are per-instance. They round-trip here and resolve to nothing
          anywhere else.
        </p>
        <p>
          <strong>Python script</strong> and <strong>Ansible playbook</strong> are
          read-only renderings, not re-importable.
        </p>
        <p>
          To bring an exported file back in, see
          <a href="/docs/workflows/import">Importing workflows</a>.
        </p>
      </dd>
      <dt>Run</dt>
      <dd>Opens the <strong>Run dialog</strong> (see below).</dd>
      <dt>Schedules / Triggers</dt>
      <dd>Shortcuts to <code>/workflows/{'{id}'}/schedules</code> and <code>/workflows/{'{id}'}/triggers</code>, the cron and webhook trigger editors respectively. See <a href="/docs/schedules">Schedules</a> and <a href="/docs/workflows/triggers">Triggers and webhooks</a>.</dd>
    </dl>
  </section>

  <section>
    <h2>Node dialog</h2>
    <p>
      Opens when you click a node on the canvas. Separate logic applies depending
      on what the node is:
    </p>

    <h3>Snippet nodes</h3>
    <p>
      Shows three tabs or sections:
    </p>
    <ul>
      <li><strong>Node config</strong> — the <code>config_overrides</code> specific to this node (rendered from the service's <code>input_schema</code>). Hidden "integration" fields (base_url, token, etc.) are filtered out because they already live on the parent integration.</li>
      <li><strong>Service</strong> — jumps into the underlying snippet's settings (name, description, target mode, timeouts). Changes here apply to every workflow using this snippet.</li>
      <li><strong>Schemas</strong> — edit the snippet's input/output JSON schemas.</li>
    </ul>
    <p>
      Node-scope changes are <strong>deferred</strong> (applied when you click
      <em>Save workflow</em>). Service/schema changes are <strong>immediate</strong>
      (they are saved to the snippet right away). Other nodes using the same
      snippet pick up the new metadata without a page reload.
    </p>

    <h3>Integration action nodes</h3>
    <p>
      For nodes whose <code>snippet_id</code> is <code>integration_action</code>.
      You configure:
    </p>
    <ul>
      <li><strong>Path parameters</strong> — one input per <code>{'{braces}'}</code> segment in the action's path. Accepts literals or <code>{'{{steps.prev.output.field}}'}</code> references.</li>
      <li><strong>Query parameters</strong> — key/value pairs for the URL query string.</li>
      <li><strong>Body</strong> — JSON textarea. Invalid JSON while you're typing is tolerated; the next valid keystroke commits.</li>
    </ul>

    <h3>Variable references</h3>
    <p>
      Strings inside <code>config_overrides</code> can include <code>&#123;&#123; … &#125;&#125;</code> templates that the executor resolves
      against upstream step outputs, the current device context, the run input, and the run's own
      metadata. Four namespaces:
    </p>
    <ul>
      <li><code>&#123;&#123; steps.&lt;node-id&gt;.output.&lt;path&gt; &#125;&#125;</code> — output of an upstream node (must be reachable from this node).</li>
      <li><code>&#123;&#123; device.&lt;path&gt; &#125;&#125;</code> — fields of the current target device. Only available in per_device mode.</li>
      <li><code>&#123;&#123; input.&lt;path&gt; &#125;&#125;</code> — the workflow's trigger payload.</li>
      <li><code>&#123;&#123; run.&lt;field&gt; &#125;&#125;</code> — metadata about the run itself (see below).</li>
    </ul>

    <h4>Run metadata</h4>
    <p>
      The <code>run</code> namespace is a fixed set of fields — anything else stays literal and fails the step.
      Its main use is failure-notification nodes, which need to say what broke without you threading it
      through <code>input</code>.
    </p>
    <ul>
      <li><code>run.id</code> · <code>run.workflow_id</code> · <code>run.workflow_name</code></li>
      <li><code>run.environment</code> (<code>draft</code>/<code>qa</code>/<code>production</code>) · <code>run.trigger</code> (<code>manual</code>/<code>schedule</code>/<code>webhook</code>/<code>subflow</code>) · <code>run.started_at</code></li>
      <li><code>run.owner_email</code> — email of whoever started the run. The default recipient of a notify-failure node.</li>
      <li><code>run.url</code> — link to this run's page.</li>
      <li><code>run.failed_step_id</code> · <code>run.failed_step_error</code> — the node that failed and its error.</li>
    </ul>
    <p>
      The two <code>failed_step_*</code> fields are filled only when the node is reached through a
      <strong>failure</strong> edge. Reached through an <code>always</code> edge they resolve to empty strings —
      the step still runs, the alert just can't name a step. <code>run.owner_email</code> is likewise empty when
      the run's starter has no email on file; for unattended workflows pass the recipient via <code>input</code>
      instead. <code>run.url</code> is built from the deployment's public frontend URL
      (<code>FRONTEND_ORIGIN</code> in <code>deploy/.env</code>); without it you get a relative path.
    </p>
    <p>Path syntax supports dotted property access and brackets — both numeric (<code>[0]</code>) and quoted string keys (<code>['router-1']</code>):</p>
    <pre><code>&#123;&#123; steps.ssh.output.results[0].output &#125;&#125;
&#123;&#123; steps.ssh.output.devices_by_name['router-1'].output.stdout &#125;&#125;
&#123;&#123; steps.ssh.output.first_success.device_name &#125;&#125;</code></pre>
    <p>
      For per_device fan-out, the aggregate envelope exposes <code>devices[]</code>,
      <code>devices_by_name</code> (keyed by hostname), <code>first_success</code>,
      <code>success_count</code>, <code>failure_count</code>, plus
      <code>successful_devices</code>/<code>failed_devices</code> arrays.
    </p>
    <h4>Filters</h4>
    <p>Append <code>| &lt;filter&gt;</code> after the path to transform the resolved value. Filters chain left-to-right:</p>
    <ul>
      <li><code>trim</code> · <code>upper</code> · <code>lower</code> · <code>truncate(N)</code></li>
      <li><code>json</code> — JSON-encodes the value (useful when embedding objects in text).</li>
      <li><code>strip_ansi</code> — removes ANSI escape sequences. <code>strip</code> = <code>strip_ansi + trim + control-char removal</code>.</li>
      <li><code>default('-')</code> — substitutes the literal when the path is missing/null/empty. Only filter that fires on a failed resolve.</li>
    </ul>
    <pre><code>&#123;&#123; steps.x.output.title | default('(untitled)') &#125;&#125;
&#123;&#123; steps.x.output.body  | strip | truncate(120) &#125;&#125;
&#123;&#123; steps.x.output       | json &#125;&#125;</code></pre>
    <p>
      <strong><code>default</code> is not a way to silence a failing step.</strong> Because it is the only filter that
      fires on a failed resolve, it is also the only one that can hide a wiring mistake. Use it for fields that are
      genuinely optional at runtime — <code>first_success</code> when every device failed, an <code>error</code> string
      that is empty on success. If the field is one the step actually needs, a failed resolve means the producer never
      emits it (or the node id / <code>target_mode</code> is wrong) and the step <em>should</em> fail: that is the only
      check standing between a broken reference and a report full of confident zeros. Quick test — if
      <code>'-'</code> isn't an acceptable value to show in that field, fix the reference instead.
    </p>
    <p>
      The node dialog runs a client-side linter against your draft: unknown step ids and unrecognised templates block <em>Apply</em>;
      unknown filter names and undeclared output fields show as warnings (schemas are advisory).
    </p>

    <h4>SSH stdout sanitisation</h4>
    <p>
      <code>steps.&lt;ssh&gt;.output.stdout</code> and <code>results[i].output</code> are ANSI-stripped by default — Nokia SR-OS,
      Cisco IOS-XR with paging, and other terminals that emit cursor-move/colour codes won't leak escape sequences into NetBox
      custom_fields, email bodies, or Slack messages. Raw bytes are always available under <code>stdout_raw</code> /
      <code>results[i].output_raw</code>. Set <code>preserve_ansi: true</code> in the SSH node's <code>config_overrides</code>
      to disable stripping for a specific node.
    </p>

    <h3>Transform nodes</h3>
    <p>
      A built-in JMESPath playground. Paste or load the last run's upstream
      output into the <em>Input</em> pane, type the expression in the middle pane,
      and see the result update live on the right. <em>Save</em> writes the
      expression back into the node's config.
    </p>

    <h3>Sentinels</h3>
    <p>
      <em>Start</em> and <em>End</em> open a read-only card explaining their role.
      They cannot be renamed or deleted.
    </p>
  </section>

  <section>
    <h2>Run dialog</h2>
    <p>
      Opens from the toolbar's green <em>Run</em> button. Two sections:
    </p>

    <h3>Runtime inputs</h3>
    <p>
      A form generated from the workflow's <code>input_schema</code>. Defaults are pulled from the schema itself.
      Appears only when the input schema has properties.
    </p>

    <h3>Target devices</h3>
    <p>
      A multi-select device picker. Required only when at least
      one node in the workflow is <code>target_mode: per_device</code>. When the
      whole workflow is <code>target_mode: once</code> (Python, transform, REST
      without device context, etc.) the section is marked <em>optional</em> and a
      note explains that empty selection runs the workflow once without a device
      context.
    </p>
    <p>
      Devices that don't allow the workflow's environment appear greyed out and
      marked <em>no &lt;environment&gt;</em>. If every target is filtered out, the
      run is rejected. The picker selects individual devices; to target a pool,
      pass <code>target_pools</code> to the run API or through a webhook.
    </p>

    <h3>Submitting</h3>
    <p>
      The submit button label adapts to your selection:
    </p>
    <ul>
      <li><em>Run on N device(s)</em> — devices selected.</li>
      <li><em>Run (no devices selected)</em> — no devices, but at least one node requires them. Submitting is still allowed so the engine can raise the real error.</li>
      <li><em>Run without devices</em> — no devices and none needed.</li>
    </ul>
    <p>
      After submit the browser navigates to
      <code>/runs/{'{id}'}/monitor</code> so you see the live execution graph.
    </p>
  </section>

  <section>
    <h2>Last run data dialog</h2>
    <p>
      Opened from the toolbar's <em>Show run data</em>. Loads the most recent
      run for this workflow plus every one of its steps in a single extra-large
      dialog.
    </p>

    <h3>Contents</h3>
    <ul>
      <li>
        <strong>Header</strong> — run id (link to the live monitor), trigger,
        started at, duration, and a status badge.
      </li>
      <li>
        <strong>Run input</strong> — the original input payload, pretty-printed.
        Hidden when empty.
      </li>
      <li>
        <strong>Steps</strong> — list of expandable rows, one per step. Each row
        shows the node id, status badge, started-at time, duration, and the
        device id when applicable. Expanding a step reveals its error (if any),
        input payload, output payload, logs, and the worker id that executed it.
      </li>
    </ul>

    <h3>Footer actions</h3>
    <ul>
      <li><em>Close</em> — dismiss.</li>
      <li><em>Refresh</em> — re-fetch the run and steps.</li>
      <li><em>Open full monitor</em> — navigates to <code>/runs/{'{id}'}/monitor</code> for the complete view.</li>
    </ul>
  </section>

  <section>
    <h2>Workflow simulation dialog</h2>
    <p>
      Opened from the toolbar's <em>Simulate</em>. A server-side static
      analysis — no handlers execute, no side effects are emitted.
    </p>
    <ul>
      <li><strong>Banner</strong> — <em>Ready to run</em> (success) or <em>Needs attention</em> (failure), with the environment, version, and node count.</li>
      <li><strong>Issues</strong> — blocking problems (unresolvable snippet, malformed edge, missing Start/End connection, etc.).</li>
      <li><strong>Warnings</strong> — non-blocking (e.g. unused outputs, very long timeouts).</li>
      <li><strong>Not covered</strong> — checks that the analyser cannot perform without executing (runtime payload validation, network reachability).</li>
    </ul>
    <p>
      The <em>Re-run</em> footer button re-queries after you've edited something
      in the editor.
    </p>
  </section>

  <section>
    <h2>Keyboard and mouse summary</h2>
    <dl>
      <dt>Click node</dt>
      <dd>Opens the node dialog.</dd>
      <dt>Drag handle → handle</dt>
      <dd>Creates a success edge.</dd>
      <dt>Right-click edge</dt>
      <dd>Edge context menu: set type or delete.</dd>
      <dt>Delete</dt>
      <dd>Removes selected nodes (except sentinels) and their edges.</dd>
      <dt>Drag palette card</dt>
      <dd>Drops a new node at the cursor.</dd>
      <dt>Wheel</dt>
      <dd>Zoom.</dd>
      <dt>Drag empty canvas</dt>
      <dd>Pan.</dd>
    </dl>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer</strong> — can open the list and the editor in read-only mode.</li>
      <li><strong>Operator</strong> — full CRUD on draft, run, promote (to qa and to production), clone and rollback.</li>
      <li><strong>Admin</strong> — same as operator. Every production promotion requires an <em>Approved by</em> name other than the submitter's own username, whatever the submitter's role.</li>
      <li>With <a href="/docs/permissions">granular permissions</a> enabled, access to a single workflow can be narrowed further from <code>/workflows/{'{id}'}/permissions</code>.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/snippets">Snippets</a> — the blocks each node calls.</li>
      <li><a href="/docs/integrations">Integrations</a> — where action nodes come from.</li>
      <li><a href="/docs/runs">Runs</a> — everything that happens after you click <em>Run</em>.</li>
      <li><a href="/docs/schedules">Schedules</a> — automate runs with cron and webhook triggers.</li>
      <li><a href="/docs/qa-lab">QA lab</a> — the 48-hour readiness gate for production promotions.</li>
      <li><a href="/docs/subflows">Subflows</a> — calling one workflow from another.</li>
      <li><a href="/docs/workflows/import">Importing workflows</a> — bringing exports back in, across instances.</li>
      <li><a href="/docs/workflows/triggers">Triggers and webhooks</a> — starting a run from an HTTP call.</li>
      <li><a href="/docs/permissions">Permissions</a> — per-workflow access beyond the base role.</li>
    </ul>
  </section>
</DocLayout>
