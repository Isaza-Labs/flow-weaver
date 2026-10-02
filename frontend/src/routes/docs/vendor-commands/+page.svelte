<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Vendor commands"
  lead="The per-platform catalog of known CLI commands. FlowWeaver checks ssh commands against it when a workflow is saved and when a step runs, and warns about anything it doesn't recognise. The agent also uses it to look up the right syntax for a vendor."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Integrate</strong> → <strong>Vendor commands</strong>
    (<a href="/vendor-commands"><code>/vendor-commands</code></a>).
  </Callout>
  <Callout tone="admin" title="Who can see and change it">
    The sidebar shows this entry to admins only. The page itself is not route-guarded, though:
    anyone with <code>vendorcommand.read</code> (every built-in role) can open the URL, and
    adding, editing or deleting entries needs <code>vendorcommand.manage</code>, which the
    built-in <code>operator</code> role also has.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      Network CLIs differ by vendor. A command that works on Cisco IOS is often wrong on
      Nokia SR Linux or Junos. The catalog records which commands are known to be valid for
      each platform (<code>device_type</code>), so that typos and cross-vendor mistakes show up
      as <strong>warnings</strong> before or while a workflow runs.
    </p>
    <Callout tone="warning" title="The catalog warns; it never blocks">
      An unknown command never stops a save or a run. It only produces a warning. To actually
      <em>forbid</em> a command, write a policy with <code>ssh_command_regex</code> (see
      <a href="#rejected">Allowed vs rejected</a>).
    </Callout>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>Know the <code>device_type</code> your devices use. It is the value in each device's <strong>Platform</strong> field (a Netmiko identifier such as <code>cisco_ios</code> or <code>nokia_srl</code>). See <a href="/docs/devices">Devices</a>.</li>
      <li>If you plan to write patterns, be comfortable with .NET-style regular expressions.</li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>
    <dl>
      <dt>Entry</dt>
      <dd>
        One row: a <code>device_type</code>, a <code>kind</code> and a <code>value</code>, plus
        optional notes. Each <code>(device_type, kind, value)</code> combination can exist only
        once.
      </dd>
      <dt>kind = exact</dt>
      <dd>
        One literal command. It is stored <strong>lowercased with runs of whitespace collapsed to
        one space</strong>, so <code>Show  Version</code> is saved as <code>show version</code>
        and matches <code>SHOW VERSION</code> too.
      </dd>
      <dt>kind = pattern</dt>
      <dd>
        A regular expression, stored as typed and matched <strong>case-insensitively</strong>
        against the normalised command (trimmed, whitespace collapsed, lowercased). It matches
        anywhere in the command unless you anchor it with <code>^…$</code>. Each match has a
        250 ms time limit; a pattern that times out is skipped.
      </dd>
      <dt>vendor_family</dt>
      <dd>
        A grouping label (<code>cisco</code>, <code>juniper</code>, <code>nokia</code>,
        <code>arista</code>, <code>huawei</code>, <code>fortinet</code>, <code>paloalto</code>,
        <code>mikrotik</code>, <code>linux</code>, <code>generic</code>), derived from the
        device_type. Unknown device_types get <code>generic</code>. The page doesn't show it,
        but the search matches it.
      </dd>
      <dt>source</dt>
      <dd>
        <code>seed</code> for shipped entries, <code>user</code> for entries added through the
        page, the API or the agent. <strong>Editing a seed entry turns it into
        <code>user</code></strong>, so later re-seeds leave it alone.
      </dd>
    </dl>

    <h3>Relation to the device platform</h3>
    <p>
      The catalog is looked up by <code>device_type</code>, ignoring case but otherwise
      exact: <code>paloalto_panos</code> and <code>panos</code> are two different platforms.
      Which device_type applies depends on when the check runs:
    </p>
    <ul>
      <li><strong>On save:</strong> only the ssh node's <code>device_type</code> config key. Workflows carry no target devices at save time, so a node without <code>device_type</code> cannot be checked.</li>
      <li><strong>At run time:</strong> the node's <code>device_type</code> if set, otherwise the target device's <strong>Platform</strong>.</li>
    </ul>

    <h3>Shipped catalog</h3>
    <p>
      On every start, the backend inserts any shipped entries that are missing. It never
      changes or removes existing rows, and it does not bring back seed entries you deleted.
      Two sources feed it:
    </p>
    <ul>
      <li>A built-in baseline for <code>cisco_ios</code>, <code>cisco_xe</code>, <code>cisco_xr</code>, <code>cisco_nxos</code>, <code>juniper_junos</code>, <code>arista_eos</code>, <code>nokia_sros</code>, <code>nokia_srl</code>, <code>huawei</code>, <code>fortinet</code> and <code>linux</code>. It includes broad read-only patterns such as <code>^show\s+\S+(\s+\S+)*$</code> (<code>display …</code> on Huawei, <code>get …</code> on Fortinet).</li>
      <li>YAML files in the backend's <code>Skills/vendors/</code> directory (currently also <code>f5_bigip</code> and <code>paloalto_panos</code>). These add missing commands and fill in each command's description and risk (<code>read</code>, <code>write</code>, <code>disruptive</code>) where it is empty.</li>
    </ul>
  </section>

  <section id="validation">
    <h2>How validation works</h2>
    <p>
      Only nodes whose snippet type is <code>ssh</code> are checked. The command list comes
      from the node's <code>command</code> (string) and <code>commands</code> (array) keys.
      For each command:
    </p>
    <ol>
      <li><strong>Skipped</strong> if it is empty or contains a template (<code>{'{{ … }}'}</code> or <code>{'${ … }'}</code>). Templated commands are never checked.</li>
      <li><strong>Deferred</strong> if the device_type is unknown or has no entries at all. You get <code>no catalog entries for device_type '&lt;dt&gt;'. Validation is deferred — …</code></li>
      <li><strong>Known</strong> if the normalised command equals an exact entry.</li>
      <li><strong>Known</strong> if any pattern for that device_type matches.</li>
      <li>Otherwise <strong>unknown</strong>. You get a warning with up to three closest exact entries (by edit distance) as <em>Did you mean</em> suggestions.</li>
    </ol>

    <h3>Where the warnings appear</h3>
    <table>
      <thead><tr><th>When</th><th>Where</th></tr></thead>
      <tbody>
        <tr><td>Workflow create or update</td><td>In the API save response's <code>warnings</code> list. The save still succeeds. The web editor does not display this list; the agent, which saves through the API, does see it.</td></tr>
        <tr><td>An ssh step runs</td><td>At the top of the step log, each line prefixed <code>WARN:</code>. The commands still run.</td></tr>
        <tr><td>Agent tool <code>validate_ssh_commands</code></td><td>Returned to the agent, which can correct typos on its own.</td></tr>
      </tbody>
    </table>

    <h3>Warning texts</h3>
    <ul>
      <li><code>node '&lt;id&gt;' command[&lt;i&gt;]: '&lt;cmd&gt;' is not a known command for device_type '&lt;dt&gt;'. Did you mean: '…'? If this is a vendor extension or custom command, add it to the vendor_commands catalog.</code></li>
      <li><code>node '&lt;id&gt;': device_type unresolved — cannot validate commands at plan time. Set config_overrides.device_type or assign target devices with a known Platform.</code></li>
      <li><code>node '&lt;id&gt;' command[&lt;i&gt;]: no catalog entries for device_type '&lt;dt&gt;'. Validation is deferred — add at least one vendor_command for this device_type to enable plan-time checks.</code></li>
      <li><code>node '&lt;id&gt;': target devices span multiple platforms (…); validating commands against each — …</code></li>
    </ul>
    <p>At run time the same texts appear without the <code>node '…'</code> prefix.</p>

    <h3>Who uses the catalog</h3>
    <ul>
      <li><strong>Workflow save</strong> and the <strong>ssh step</strong>, as above.</li>
      <li><strong>Agent tools</strong> (they need <code>vendorcommand.read</code>): <code>list_vendor_commands</code>; <code>validate_ssh_commands</code>; <code>find_command</code>, which ranks <em>exact</em> entries by word overlap with a plain-language task and can filter by risk. The agent's ssh skill tells it to look up commands before drafting nodes for any vendor other than <code>cisco_ios</code> or <code>linux</code>, and to validate them afterwards.</li>
      <li><strong>Agent write tools</strong> <code>create_vendor_command</code>, <code>update_vendor_command</code>, <code>delete_vendor_command</code> (they need <code>vendorcommand.manage</code> and ask you to confirm first).</li>
    </ul>
    <p>
      Each server process caches the catalog for up to 60 seconds. A change made on this page
      applies at once in the process that saved it; other processes, such as the worker that
      runs ssh steps, pick it up within a minute.
    </p>
  </section>

  <section id="rejected">
    <h2>Allowed vs rejected</h2>
    <p>The catalog only holds <em>known</em> commands. There are no deny entries.</p>
    <table>
      <thead><tr><th>Outcome</th><th>Why</th><th>What happens</th></tr></thead>
      <tbody>
        <tr><td>Accepted, silently</td><td>Matches an exact entry or a pattern, or contains a template.</td><td>Runs.</td></tr>
        <tr><td>Accepted with a warning</td><td>Not in the catalog, or the device_type has no entries.</td><td>Runs; the warning is in the save response or step log.</td></tr>
        <tr><td><strong>Blocked</strong></td><td>An enabled <a href="/docs/policies">policy</a> with <code>ssh_command_regex</code> matches the command.</td><td>That command and every command after it in the step are not sent. The result entry has <code>blocked_by_policy: true</code> and the error <code>blocked by policy `&lt;name&gt;`: &lt;reason&gt;</code>. The step fails.</td></tr>
      </tbody>
    </table>
    <p>
      Deleting a catalog entry therefore does <strong>not</strong> ban the command; it only
      makes it produce a warning. A broad pattern such as <code>^show\s+…</code> also means
      that deleting a single exact <code>show</code> entry changes nothing.
    </p>
  </section>

  <section>
    <h2>The screen and its fields</h2>
    <dl>
      <dt>Header</dt><dd><strong>Refresh</strong>, <strong>Add device type</strong>, <strong>New entry</strong> (toggles the form).</dd>
      <dt>Filter by device_type</dt>
      <dd>
        <em>All device types</em> or one platform. The list reloads from the server as soon as
        you change it. The options combine the shipped list, every device_type present in the
        loaded entries, and types you added in this browser.
      </dd>
      <dt>Search</dt>
      <dd>
        Filters the <strong>loaded</strong> entries by command, notes, device_type, kind or
        vendor family. A counter shows the matches, or the total in the catalog when you
        aren't searching.
      </dd>
      <dt>Entry row</dt>
      <dd>
        Badges for device_type, kind (<em>exact</em> or <em>pattern</em>) and source
        (<em>seed</em> or <em>user</em>), then the value and notes. Click the row to expand the
        edit form. The trash icon deletes.
      </dd>
      <dt>New entry form</dt>
      <dd>
        <strong>device_type</strong>, <strong>kind</strong> (<em>exact (literal command)</em> or
        <em>pattern (regex)</em>), <strong>Command</strong> or <strong>Pattern (regex, evaluated
        case-insensitively)</strong>, and <strong>Notes (optional)</strong>. Buttons:
        <strong>Cancel</strong>, <strong>Add</strong>.
      </dd>
      <dt>Edit form</dt><dd>The same fields, with <strong>Save changes</strong>.</dd>
      <dt>Add device type dialog</dt>
      <dd>
        One <strong>device_type</strong> field (placeholder <code>e.g. paloalto_panos</code>).
        The value is lowercased and may contain only lowercase letters, digits and
        underscores. Clicking <strong>Add device type</strong> adds it to the pickers
        <em>in this browser only</em> and opens the New entry form with it selected. The type
        only becomes real, for everyone, once you save at least one entry for it.
      </dd>
    </dl>
    <p>
      The page shows the first 200 entries (the server maximum). With <em>All device
      types</em> selected on a large catalog, entries beyond 200 are not shown or
      searchable; pick a device_type to narrow the list. The footer shows
      <em>&lt;shown&gt; of &lt;total&gt;</em>.
    </p>
    <p>
      The page does not show or edit an entry's description or risk (<code>read</code>,
      <code>write</code>, <code>disruptive</code>). Those come from the YAML files and are what
      <code>find_command</code> ranks on. Entries you add here have neither, so
      <code>find_command</code> ranks them by command text only.
    </p>
  </section>

  <section>
    <h2>Main procedure</h2>

    <h3>Add a command to an existing platform</h3>
    <ol>
      <li>Click <strong>New entry</strong>.</li>
      <li>Choose the <strong>device_type</strong> and <strong>kind</strong>.</li>
      <li>Type the command or pattern. Add notes explaining why, for example a ticket reference.</li>
      <li>Click <strong>Add</strong>. The toast reads <em>Added &lt;kind&gt; for &lt;device_type&gt;</em>.</li>
    </ol>

    <h3>Add a new platform</h3>
    <ol>
      <li>Click <strong>Add device type</strong>, enter the identifier exactly as your devices' Platform field has it, and confirm.</li>
      <li>In the New entry form that opens, add at least one command and click <strong>Add</strong>. Until then, the platform exists only in your browser.</li>
      <li>Add a read-only pattern (for example <code>^show\s+\S+(\s+\S+)*$</code>) if the platform has a <code>show</code>-style family. Otherwise every <code>show</code> variant will produce a warning.</li>
    </ol>

    <h3>Edit or remove</h3>
    <ol>
      <li>Click the row, change the fields, then <strong>Save changes</strong>. Editing a seed entry makes it <em>user</em>.</li>
      <li>To remove an entry, click the trash icon and confirm <em>Delete catalog entry?</em>. The message warns that the validator will flag the command on future saves.</li>
    </ol>
  </section>

  <section>
    <h2>Worked examples</h2>

    <h3>Why was my command flagged?</h3>
    <p>
      <strong>Situation:</strong> an ssh node with <code>"device_type": "nokia_srl"</code> and
      <code>"commands": ["show ip bgp summary"]</code>. The save response (as the agent sees it) contains:
    </p>
    <pre><code>{`node 'check_bgp' command[0]: 'show ip bgp summary' is not a known command
for device_type 'nokia_srl'. Did you mean: '...'? If this is a vendor
extension or custom command, add it to the vendor_commands catalog.`}</code></pre>
    <p>
      <strong>Reading it:</strong> the device_type resolved, the catalog has entries for
      it, and neither an exact entry nor a pattern matched. On this platform that is usually a
      Cisco-style command used by mistake. Filter the page by <code>nokia_srl</code> and
      search for <code>bgp</code> to find the right one, then fix the node. The workflow was
      saved either way.
    </p>

    <h3>Allow a vendor extension with a pattern</h3>
    <p>
      <strong>Prerequisites:</strong> <code>vendorcommand.manage</code>, and devices whose
      Platform is <code>cisco_xr</code>.
    </p>
    <ol>
      <li><strong>New entry</strong> → device_type <code>cisco_xr</code>, kind <code>pattern</code>.</li>
      <li>Pattern: <code>^admin\s+show\s+\S+(\s+\S+)*$</code>. Notes: <code>&lt;ticket reference&gt;</code>.</li>
      <li><strong>Add</strong>.</li>
    </ol>
    <p>
      <strong>Expected result:</strong> for <code>admin show platform</code> on
      <code>cisco_xr</code>, neither the save response nor the ssh step log (<code>WARN:</code>
      lines) mentions the command any more.
    </p>
    <p>
      <strong>Common error:</strong> <code>pattern is not a valid regex: …</code>, for example
      from an unbalanced parenthesis. <strong>Recovery:</strong> fix the expression; nothing
      was saved.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li><code>vendorcommand.read</code> (viewer and above): list and view. The agent's lookup tools need it as well.</li>
      <li><code>vendorcommand.manage</code> (operator and above by default): create, edit, delete.</li>
      <li>Every create, update and delete is written to the <a href="/docs/admin/audit">audit log</a> (entity <code>vendor_command</code>, with the old and new value) and to traces (<code>vendor_command.create</code> / <code>.update</code> / <code>.delete</code>).</li>
      <li>Adding a broad pattern silences warnings for a whole family of commands. Review patterns as carefully as you review policies. The catalog is advisory, but people and the agent rely on its silence.</li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li><strong>Loading:</strong> a spinner.</li>
      <li><strong>No entries for the filter:</strong> <em>No vendor commands match</em>, with <em>Catalog has no entries for &lt;dt&gt; yet. Add one or clear the filter.</em></li>
      <li><strong>Catalog completely empty:</strong> the same title, explaining that every ssh command will get a "validation deferred" warning.</li>
      <li><strong>Search with no hits:</strong> <em>No entries match your search</em>, with <strong>Clear search</strong>.</li>
      <li><strong>Load failure:</strong> a red alert with the error and the toast <em>Couldn’t load vendor commands</em>.</li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>Message</th><th>Cause</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr><td><code>value is required</code></td><td>Empty command or pattern.</td><td>Type a value.</td></tr>
        <tr><td><code>device_type is required</code></td><td>Empty device_type (API).</td><td>Pick one.</td></tr>
        <tr><td><code>kind must be 'exact' or 'pattern'</code></td><td>Invalid kind (API or agent).</td><td>Use one of the two.</td></tr>
        <tr><td><code>pattern is not a valid regex: …</code></td><td>The regex does not parse.</td><td>Fix the expression.</td></tr>
        <tr><td><code>vendor_command already exists for device_type='…', kind='…', value='…'</code></td><td>Duplicate. Remember that exact values are lowercased first.</td><td>Keep the existing entry, or edit it.</td></tr>
        <tr><td><code>vendor_command not found</code></td><td>The entry was deleted meanwhile.</td><td>Click <strong>Refresh</strong>.</td></tr>
        <tr><td><em>Enter a device_type identifier.</em> / <em>Use lowercase letters, digits and underscores only (e.g. cisco_ios).</em></td><td>Add device type dialog validation.</td><td>Correct the identifier.</td></tr>
        <tr><td><em>Couldn’t save vendor command</em></td><td>Save failed, for example editing an entry into a duplicate or lacking permission.</td><td>Read the toast detail and adjust.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Impact, reverting and recovery</h2>
    <ul>
      <li>Changes affect only <em>warnings</em>, starting with the next save or step run. Already-saved workflows are not re-checked until they are saved again.</li>
      <li>Deleting an entry has no confirmation beyond the dialog and cannot be undone from the page. To revert, add the entry again. It comes back as <code>user</code>. A deleted <em>seed</em> entry is not restored on restart.</li>
      <li>Editing a seed entry turns it into <code>user</code> for good. Shipped updates to that command will no longer apply.</li>
      <li>Custom device types that have no saved entry exist only in your browser's local storage. Clearing site data removes them.</li>
    </ul>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>No deny entries; blocking needs a policy.</li>
      <li>Saving a workflow cannot use target devices to resolve the platform, so nodes without <code>device_type</code> are only checked at run time.</li>
      <li>Templated commands are never checked.</li>
      <li>The workflow editor does not show save-time warnings. In the UI, look for <code>WARN:</code> lines in the ssh step log after a run.</li>
      <li>Description, risk and vendor family are not editable on the page.</li>
      <li>At most 200 entries are loaded; search covers only those.</li>
      <li>Deleting an entry shows no success toast; the row simply disappears.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/policies">Policies</a> — actually blocking commands with <code>ssh_command_regex</code>.</li>
      <li><a href="/docs/snippets">Snippets</a> — the <code>ssh</code> snippet and its <code>device_type</code>, <code>command</code> and <code>commands</code> keys.</li>
      <li><a href="/docs/devices">Devices</a> — the Platform field used at run time.</li>
      <li><a href="/docs/ai">AI</a> — the agent that uses <code>find_command</code> and <code>validate_ssh_commands</code>.</li>
    </ul>
  </section>
</DocLayout>
