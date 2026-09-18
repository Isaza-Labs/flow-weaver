<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Subflows"
  lead="Reuse one workflow as a single step inside others. A subflow is an ordinary workflow with a reusable tag; a subflow node in another workflow runs it as a child run."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Build</strong> → <strong>Subflows</strong>
    (<a href="/subflows"><code>/subflows</code></a>, page title <em>Reusable subflows</em>).
    The tag itself is set in the workflow editor: <strong>More</strong> menu →
    <strong>Reusable as subflow</strong>. The node is in the editor palette under
    <strong>Composition</strong> → <strong>Subflow</strong>.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      When several workflows need the same sequence of steps — a pre-check, a backup,
      a notification — build it once as a workflow, tag it as reusable, and call it from
      each parent through a <strong>subflow node</strong>. The parent sees the whole child
      run as one step with one result.
    </p>
    <p>
      The <code>/subflows</code> page is an index: it lists every tagged workflow and lets
      you open or delete one. Tagging, untagging and wiring happen in the workflow editor.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>You need a workflow that already works on its own. Run it directly first; a child that fails on its own will fail inside a parent too.</li>
      <li>To tag or untag a workflow you need <code>workflow.update</code> on it. To add a subflow node to a parent you need <code>workflow.update</code> on the parent.</li>
      <li>Decide which environment the child should live in <em>before</em> you promote anything — see <a href="#environments">Environments and versions</a>.</li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>
    <dl>
      <dt>Subflow (the definition)</dt>
      <dd>
        A normal workflow whose <code>metadata.is_subflow</code> is <code>true</code>.
        Nothing else differs: it has its own nodes, versions, environment, triggers and
        run history, and you can still run it directly.
      </dd>
      <dt>Subflow node (the call)</dt>
      <dd>
        A node in a parent workflow whose <code>snippet_id</code> is the literal
        <code>subflow</code>. Its config names the child by id in
        <code>subflow_workflow_id</code>. It has no snippet behind it, so the node dialog
        only shows the <strong>Node</strong> tab.
      </dd>
      <dt>Child run</dt>
      <dd>
        When the parent reaches the node, the engine creates a separate run of the child
        with trigger <code>subflow</code>. The child appears in <a href="/docs/runs">Runs</a>
        as its own run. The parent step stays open until the child finishes.
      </dd>
    </dl>

    <h3>What the child inherits</h3>
    <ul>
      <li><strong>Target devices and pools</strong> — the child runs against the parent run's targets. You cannot pick different targets on the node.</li>
      <li><strong>Who started it</strong> — the child records the parent's starter, so <code>{'{{ run.owner_email }}'}</code> resolves to the same person.</li>
      <li><strong>Input</strong> — the parent run's input, with the node's whole config merged on top (see <a href="#mapping">Inputs and outputs</a>).</li>
    </ul>
    <p>
      The child resolves its targets against <em>its own</em> environment, not the parent's.
      A device that does not allow the child's environment is dropped, and the child fails
      if no target is left.
    </p>

    <h3>Reference by id</h3>
    <p>
      The node stores the child's workflow <strong>id</strong>, not its name. Renaming the
      child is safe. The node also stores <code>subflow_name</code> and
      <code>subflow_environment</code>, but only so the canvas can label the node. The engine
      ignores them, so the canvas label can go stale after a rename.
    </p>

    <h3>Cycles</h3>
    <p>
      A subflow may not call itself, directly or through other subflows. FlowWeaver checks
      this twice:
    </p>
    <ul>
      <li><strong>When the run is requested</strong>, it walks the stored graphs. A cycle rejects the run with <code>dag_invalid</code> and the message <code>subflow cycle detected — workflow &lt;id&gt; references itself transitively</code>. No run is created.</li>
      <li><strong>When the node fires</strong>, it checks the chain of parent runs (up to 32 levels). This catches a graph edited after the run started. The step fails with code <code>subflow_cycle</code> and nothing runs for that node.</li>
    </ul>
    <p>Saving a workflow does <em>not</em> check for cycles; only running it does.</p>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Reusable subflows (<code>/subflows</code>)</h3>
    <dl>
      <dt>New workflow</dt><dd>Opens <code>/workflows/new</code>. A new workflow is not tagged; tag it after you build it.</dd>
      <dt>Refresh</dt><dd>Reloads the list.</dd>
      <dt>Environment</dt>
      <dd>
        <em>All</em>, <em>Draft</em>, <em>QA</em> or <em>Production</em>. The list does not
        change until you click <strong>Apply</strong>.
      </dd>
      <dt>Table</dt>
      <dd>
        <strong>Name</strong>, <strong>Environment</strong>, <strong>Version</strong>
        (<code>v&lt;n&gt;</code>) and <strong>Updated</strong>, sorted by name. Each row has
        <strong>Open</strong> (goes to the editor) and <strong>Delete</strong>.
      </dd>
    </dl>
    <p>
      The page has no text search and no column showing which workflows use a subflow. To
      find one by name, use the environment filter and scan the list, which is sorted by
      name.
    </p>

    <h3>Reusable as subflow (editor)</h3>
    <p>
      In the editor toolbar, open <strong>More</strong>. The last entry,
      <strong>Reusable as subflow</strong>, shows <code>ON</code> or <code>OFF</code>.
      Clicking it saves the change right away; you don't need to click Save. The toasts are
      <em>Tagged as reusable subflow</em> and <em>Removed subflow tag</em>.
    </p>

    <h3>Subflow node dialog</h3>
    <dl>
      <dt>Subflow</dt>
      <dd>
        A dropdown of every tagged workflow in any environment, shown as
        <code>name (v&lt;n&gt; · environment)</code>. Choose
        <em>— choose a subflow —</em> to clear it.
      </dd>
      <dt>Selected subflow</dt>
      <dd>
        Name, version and environment of the choice, an <strong>Open</strong> link, and
        <strong>Inputs the subflow expects</strong>, which shows the child's
        <code>input_schema</code>. That schema is often empty (<code>{'{}'}</code>) because
        most workflows read <code>{'{{ input.X }}'}</code> without declaring it; open the
        child to see which keys it actually reads.
      </dd>
      <dt>Input mapping</dt>
      <dd>
        A JSON object stored under the node's <code>input</code> key. The textarea ignores
        invalid JSON until you finish typing, so check that the text parses before you click
        Apply.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Main procedure</h2>

    <h3>1. Make a workflow reusable</h3>
    <ol>
      <li>Open the workflow in the editor (<code>/workflows/&lt;id&gt;</code>).</li>
      <li>Click <strong>More</strong> → <strong>Reusable as subflow</strong>. The entry changes to <code>ON</code>.</li>
      <li>Check that the workflow is listed at <code>/subflows</code>.</li>
    </ol>

    <h3>2. Call it from a parent</h3>
    <ol>
      <li>Open the parent workflow.</li>
      <li>In the palette, drag <strong>Composition</strong> → <strong>Subflow</strong> onto the canvas. It appears as <em>Subflow (pick one)</em>.</li>
      <li>Click the node. In <strong>Subflow</strong>, choose the child.</li>
      <li>Under <strong>Input mapping</strong>, enter the values the child needs (see below).</li>
      <li>Click Apply in the dialog, connect the node with edges, then <strong>Save</strong> the workflow.</li>
    </ol>

    <h3 id="mapping">3. Inputs and outputs</h3>
    <p>
      The child run's input is the parent run's input with the node's
      <strong>entire config</strong> merged on top. Config keys win when the names clash.
      The Input mapping object is one of those config keys, so inside the child it is under
      <code>input.input</code>:
    </p>
    <table>
      <thead><tr><th>Where the value comes from</th><th>How the child reads it</th></tr></thead>
      <tbody>
        <tr><td>Parent run input key <code>site</code></td><td><code>{'{{ input.site }}'}</code></td></tr>
        <tr><td>Input mapping key <code>hostname</code></td><td><code>{'{{ input.input.hostname }}'}</code></td></tr>
        <tr><td>The node's own <code>subflow_workflow_id</code></td><td><code>{'{{ input.subflow_workflow_id }}'}</code> (it is merged in as well)</td></tr>
      </tbody>
    </table>
    <p>
      Values in the node config can reference upstream steps of the parent with
      <code>{'{{ steps.<node>.output.… }}'}</code>. These are the <em>only</em> templates the
      subflow node resolves. <code>{'{{ input.… }}'}</code>, <code>{'{{ run.… }}'}</code> and
      <code>{'{{ device.… }}'}</code> are not resolved here and cause the step to fail (see
      Errors). The parent's input already reaches the child, so you don't need to map it.
    </p>
    <p>When the child finishes, the subflow step's output is:</p>
    <pre><code>{`{
  "run_id": "<child run id>",
  "status": "completed",
  "final_state": "<final state>",
  "error": "<error message>",
  "steps": {
    "<child node id>": { "<field>": "<that step's output>" }
  }
}`}</code></pre>
    <ul>
      <li><code>status</code> is <code>completed</code>, <code>failed</code> or <code>cancelled</code>.</li>
      <li><code>final_state</code> is <code>null</code> if the child was never scored.</li>
      <li><code>error</code> is present only when the child recorded one.</li>
    </ul>
    <p>
      Downstream nodes in the parent read it as
      <code>{'{{ steps.<subflow node>.output.steps.<child node>.<field> }}'}</code> or check
      <code>{'{{ steps.<subflow node>.output.status }}'}</code>.
    </p>
    <Callout tone="warning" title="Output shape changed">
      Older versions put the child's step map at the top level
      (<code>output.&lt;child node&gt;</code>). Templates written that way need
      <code>.steps</code> inserted before the child node id.
    </Callout>
    <p>
      The step succeeds when the child run completes. A failed or cancelled child makes the
      step fail, so a <code>failure</code> edge out of the subflow node fires. The step also
      takes on the child's rollback tier: if the child sent an email, the parent's subflow
      step counts as non-reversible.
    </p>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Goal:</strong> reuse a "backup running config" workflow from a
      "change interface description" workflow.
    </p>
    <p><strong>Prerequisites:</strong></p>
    <ul>
      <li>A workflow <code>backup-running-config</code> (placeholder name) that runs on its target devices and writes a file. It reads a ticket reference as <code>{'{{ input.input.ticket }}'}</code>.</li>
      <li>A parent workflow <code>change-interface-description</code> in the same environment, with an upstream node <code>precheck</code> whose output has a <code>ticket</code> field.</li>
    </ul>
    <ol>
      <li>Open <code>backup-running-config</code> → <strong>More</strong> → <strong>Reusable as subflow</strong> (ON).</li>
      <li>Open <code>change-interface-description</code>, drag <strong>Subflow</strong> onto the canvas after <code>precheck</code>, and choose <code>backup-running-config</code>.</li>
      <li>Set <strong>Input mapping</strong> to:
        <pre><code>{`{
  "ticket": "{{ steps.precheck.output.ticket }}"
}`}</code></pre>
      </li>
      <li>Apply, connect <code>precheck → subflow node → the change step</code>, and save.</li>
      <li>Run the parent against one device.</li>
    </ol>
    <p>
      <strong>Expected result:</strong> <a href="/runs"><code>/runs</code></a> shows two runs,
      the parent and a child with trigger <code>subflow</code> on the same device. The parent's
      subflow step completes when the child does. Its output has <code>status</code>
      <code>completed</code> and the backup step's output under <code>steps</code>.
    </p>
    <p>
      <strong>Common error:</strong> the subflow step fails with
      <code>unresolved_template</code> and
      <code>subflow unresolved template references — …</code>. This usually means the mapping
      used <code>{'{{ input.ticket }}'}</code> or pointed at a step id that does not exist.
      <strong>Recovery:</strong> take the value from an upstream step
      (<code>{'{{ steps.… }}'}</code>), or delete the mapping entry if the value is already in
      the parent's input. The child sees the parent's input anyway.
    </p>
  </section>

  <section id="environments">
    <h2>Environments and versions</h2>
    <ul>
      <li>A subflow node points at <strong>one specific workflow id</strong>. That id belongs to one environment and one row, and the node always runs that row's <em>current</em> graph. Nothing is pinned to a version.</li>
      <li>Promotion creates a <strong>new copy with a new id</strong> in the target environment. The copy keeps the source's nodes as they are, so a promoted parent still calls the child id it had in draft. Promoting the parent does not promote the child, and does not repoint the node.</li>
      <li>The copy also keeps the source's metadata. A child promoted while tagged is tagged in the new environment too.</li>
      <li>Production workflows cannot be edited or deleted (<code>production_immutable</code>). You therefore cannot toggle the tag on a production row; set it before you promote.</li>
      <li>The picker lists tagged workflows from <em>every</em> environment. Check the environment shown next to the name before you pick.</li>
    </ul>
    <Callout tone="warning" title="Editing a subflow changes every caller">
      Callers run the child's current graph. Saving a change to the child affects every parent
      on its next run, with no confirmation and no list of affected parents. Test the change by
      running the child directly, then run one parent before relying on it.
    </Callout>
  </section>

  <section>
    <h2>Dependencies and impact of changes</h2>
    <p>
      FlowWeaver has no <em>used by</em> view for subflows. To find the callers before you
      change a child, open each candidate parent and look for subflow nodes. Their canvas
      subtitle shows <code>subflow · &lt;environment&gt;</code>, and their dialog shows the
      target.
    </p>
    <table>
      <thead><tr><th>Action on the child</th><th>Effect on existing callers</th><th>How to revert</th></tr></thead>
      <tbody>
        <tr>
          <td>Untag (<strong>Reusable as subflow</strong> → OFF)</td>
          <td>None. Callers reference the id and keep working. The child disappears from <code>/subflows</code> and from the picker, so no new node can select it.</td>
          <td>Toggle it back ON.</td>
        </tr>
        <tr>
          <td>Rename</td>
          <td>None at run time. Canvas labels in parents keep the old name until the node is re-picked.</td>
          <td>Rename back.</td>
        </tr>
        <tr>
          <td>Edit nodes or config</td>
          <td>Every caller runs the new graph on its next run.</td>
          <td>Undo the edit in the child and save again. A production row cannot be edited at all; a new production version is a new copy with a new id, so callers must be re-pointed to it.</td>
        </tr>
        <tr>
          <td>Delete</td>
          <td>Every caller breaks. A parent run is refused with <code>dag_invalid</code> (<code>subflow references non-existent workflow &lt;id&gt;</code>). If the node is reached anyway, it fails with <code>subflow_missing</code>.</td>
          <td>You can't undo a delete from the UI. Rebuild or re-import the child (it gets a new id), then re-pick it in every parent.</td>
        </tr>
      </tbody>
    </table>
    <p>
      <strong>Delete</strong> on <code>/subflows</code> asks for confirmation (<em>Delete subflow
      "…"?</em>) and deletes <strong>the workflow itself</strong>, not just the tag. To remove a
      workflow from the list only, untag it instead. You cannot delete a production workflow.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li><strong>View the list and pick a subflow:</strong> <code>workflow.read</code>.</li>
      <li><strong>Tag or untag, add a subflow node:</strong> <code>workflow.update</code>. With granular permissions on, you also need an editor grant on that workflow.</li>
      <li><strong>Delete:</strong> <code>workflow.delete</code>. With granular permissions on, you also need an owner grant on the workflow. Otherwise the request fails with <code>missing_owner_grant</code>.</li>
      <li><strong>Run a parent:</strong> <code>workflow.run</code> on the parent. The child is started by the engine on the parent's behalf. Your permissions on the child itself are not checked at that point, so tagging a workflow effectively lets anyone who can run a caller execute it.</li>
      <li>Policies can match subflow nodes: in a rule, the snippet type <code>subflow</code> matches the node's literal <code>snippet_id</code>. See <a href="/docs/policies">Policies</a>.</li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li><strong>Loading:</strong> a spinner labelled <em>Loading subflows…</em>.</li>
      <li><strong>Nothing tagged</strong> (or nothing in the chosen environment): <em>No subflows yet</em>, with the hint to open a workflow and tick <em>Reusable as subflow</em>.</li>
      <li><strong>In the node dialog:</strong> <em>Loading available subflows…</em> while it loads. If nothing is tagged, a warning reads <em>No workflows tagged as subflows yet</em> and links to <strong>View catalogue</strong>.</li>
      <li><strong>Unconfigured node:</strong> the canvas shows <em>Subflow (pick one)</em> and <em>Click to choose the target subflow</em>.</li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>Message or code</th><th>Meaning</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr>
          <td><em>Failed to load subflows</em></td>
          <td>The list request failed.</td>
          <td>Click <strong>Refresh</strong>. If it keeps failing, check your session and <code>workflow.read</code>.</td>
        </tr>
        <tr>
          <td><code>subflow_missing</code> — <code>needs config_overrides.subflow_workflow_id …</code></td>
          <td>The node was saved without a target.</td>
          <td>Open the node, choose a <strong>Subflow</strong>, save.</td>
        </tr>
        <tr>
          <td><code>subflow_missing</code> — <code>names workflow &lt;id&gt;, which does not exist or is not active</code></td>
          <td>The child was deleted. There is no child run to inspect.</td>
          <td>Re-pick a valid child in the node.</td>
        </tr>
        <tr>
          <td><code>subflow_failed</code> — <code>subflow run &lt;id&gt; failed at step &lt;node&gt;: …</code></td>
          <td>The child ran and failed.</td>
          <td>Open the child run in <a href="/docs/runs">Runs</a> and fix the failing step. Add a <code>failure</code> edge in the parent if the parent should react.</td>
        </tr>
        <tr>
          <td><code>subflow_cycle</code></td>
          <td>Starting the child would re-enter a workflow already running above it.</td>
          <td>Remove the node that closes the loop.</td>
        </tr>
        <tr>
          <td><code>dag_invalid</code> — <code>subflow cycle detected …</code> / <code>subflow references non-existent workflow …</code></td>
          <td>The run was refused before it started.</td>
          <td>Fix the loop, or re-pick the deleted child, then run again.</td>
        </tr>
        <tr>
          <td><code>unresolved_template</code> — <code>subflow unresolved template references …</code></td>
          <td>A template in the node config did not resolve. It lists up to 10 locations.</td>
          <td>Use only <code>{'{{ steps.… }}'}</code> references to upstream steps that exist.</td>
        </tr>
        <tr>
          <td><code>production_immutable</code> (409)</td>
          <td>You tried to tag, untag or delete a production workflow.</td>
          <td>Change the draft and promote again.</td>
        </tr>
        <tr>
          <td><em>Couldn’t delete subflow</em> — <code>missing_owner_grant</code></td>
          <td>Granular permissions are on and you lack an owner grant.</td>
          <td>Ask an owner or an admin.</td>
        </tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>No search box and no "used by" list on <code>/subflows</code>.</li>
      <li>No version pinning: a node always runs the child's current graph.</li>
      <li>The child's targets cannot be set per node; they are always the parent's.</li>
      <li><strong>Inputs the subflow expects</strong> shows only the declared <code>input_schema</code>, which is usually empty.</li>
      <li>The Input mapping value ends up under <code>input.input</code> in the child, not at the top level.</li>
      <li>A newly dropped node also carries an empty <code>inputs</code> key, which is unused and harmless.</li>
      <li>The run pages do not link parent and child. Match them with the child's <code>run_id</code> in the subflow step output.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — the editor, templates, promotion and versions.</li>
      <li><a href="/docs/runs">Runs</a> — reading parent and child runs.</li>
      <li><a href="/docs/policies">Policies</a> — rules that match the <code>subflow</code> snippet type.</li>
      <li><a href="/docs/qa-lab">QA lab</a> — environments and device allow flags that affect the child's targets.</li>
    </ul>
  </section>
</DocLayout>
