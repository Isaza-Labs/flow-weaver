<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Python packages"
  lead="The allow-list of extra modules that python_snippet scripts may import. A stdlib entry only needs allowing; a pip entry is installed by the worker first."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Govern</strong> → <strong>Python packages</strong>
    (<a href="/admin/python-packages"><code>/admin/python-packages</code></a>).
  </Callout>
  <Callout tone="admin" title="Admin only">
    The page and every <code>/api/admin/python-modules</code> endpoint require the
    <code>admin</code> role. An allow-list entry loosens a sandbox rule, so an
    admin makes the final call on each one.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      Before a <code>python_snippet</code> runs, its code is checked statically,
      and the script is refused if it imports any module outside the allow-list.
      This page extends that list beyond the built-in modules. Each entry is either
      a <strong>stdlib</strong> module already on the interpreter, or a
      <strong>pip</strong> package that the worker downloads from PyPI and installs.
    </p>
    <p>
      The <a href="/docs/snippets#extra-imports">Snippets</a> chapter has a short
      summary. This chapter is the full reference.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>You need the <code>admin</code> role.</li>
      <li>
        For a pip package, know its <strong>PyPI name</strong> and the name
        scripts <strong>import</strong>. They often differ.
      </li>
      <li>
        A <strong>worker</strong> process must be running on Linux, for example the
        <code>worker</code> service of the Docker deployment. Only that process
        installs packages. If it isn't running, pip entries stay
        <code>pending</code>.
      </li>
      <li>
        Unless an operator changed the setting, the package must be available as a
        <strong>wheel</strong> (see <a href="#concepts">Concepts and limits</a>).
      </li>
    </ul>
  </section>

  <section id="concepts">
    <h2>Concepts and limits</h2>

    <h3>Built-in modules (always allowed)</h3>
    <p>
      Every snippet can import these, with no entry on this page. They are listed
      in a card at the bottom of the page:
    </p>
    <p>
      <code>json</code>, <code>math</code>, <code>datetime</code>, <code>re</code>,
      <code>ipaddress</code>, <code>flowweaver_runtime</code>, <code>csv</code>,
      <code>io</code>, <code>base64</code>, <code>string</code>,
      <code>collections</code>, <code>itertools</code>, <code>hashlib</code>,
      <code>time</code>.
    </p>
    <p>
      <a href="/docs/snippets#network-enabled">Network-enabled</a> snippets can
      additionally import <code>netmiko</code>, <code>paramiko</code>,
      <code>socket</code>, <code>select</code>, <code>textfsm</code>,
      <code>ntc_templates</code> and <code>logging</code>. That extra set is not
      configured here and doesn't appear on the page.
    </p>

    <h3>Two sources</h3>
    <dl>
      <dt>stdlib</dt>
      <dd>
        The module ships with the interpreter (for example
        <code>statistics</code> or <code>decimal</code>). Nothing is installed.
        The entry is <code>ready</code> as soon as it is saved. FlowWeaver does
        <strong>not</strong> check that the module exists, so a typo gives a
        <code>ready</code> row and a script that fails when it tries to import.
      </dd>
      <dt>pip</dt>
      <dd>
        A PyPI package. The entry starts as <code>pending</code>, and the worker
        runs <code>pip install</code> in the background.
      </dd>
    </dl>

    <h3>Import name vs PyPI package</h3>
    <p>Each entry keeps two separate names:</p>
    <dl>
      <dt>Import name</dt>
      <dd>
        The name a script writes after <code>import</code>. Only the top-level
        name counts: for <code>import yaml.loader</code> the check looks at
        <code>yaml</code>. For a <strong>stdlib</strong> entry, this must be a
        Python identifier (letters, digits and <code>_</code>, no dots).
      </dd>
      <dt>PyPI package (pip_spec)</dt>
      <dd>
        The requirement passed to pip, which can include a version pin or extras,
        for example <code>Django==5.0</code> or <code>beautifulsoup4</code>. It
        defaults to the import name. Allowed characters are letters, digits and
        <code>. _ - [ ] = &lt; &gt; ! ~ ,</code>. Spaces aren't allowed, so a
        spec holds exactly one requirement.
      </dd>
    </dl>
    <table>
      <thead><tr><th>Script writes</th><th>PyPI package</th></tr></thead>
      <tbody>
        <tr><td><code>import bs4</code></td><td><code>beautifulsoup4</code></td></tr>
        <tr><td><code>import yaml</code></td><td><code>PyYAML</code></td></tr>
        <tr><td><code>import dateutil</code></td><td><code>python-dateutil</code></td></tr>
        <tr><td><code>import PIL</code></td><td><code>Pillow</code></td></tr>
      </tbody>
    </table>
    <Callout tone="info" title="The import name is discovered for pip entries">
      For a <strong>pip</strong> entry, the server also accepts a package name
      (letters, digits and <code>. _ -</code>) in <strong>Import name</strong>,
      even though the form's hint says "Top-level module only". After the install,
      the worker asks the installed package which module it provides and renames
      the entry to match. For example, an entry typed as
      <code>python-dateutil</code> ends up with import name
      <code>dateutil</code>.
    </Callout>

    <h3>Status lifecycle</h3>
    <table>
      <thead><tr><th>Status</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td><code>pending</code></td><td>Waiting for the worker to pick the entry up. Every new pip entry starts here, and so does a retried one.</td></tr>
        <tr><td><code>installing</code></td><td>A worker has claimed the entry and is running pip. If a claim is older than 10 minutes, another worker takes it over.</td></tr>
        <tr><td><code>ready</code></td><td>The package installed and imported correctly. <strong>Only <code>ready</code> entries can be imported.</strong> The <em>Installed</em> column shows the package's <code>__version__</code> when it has one.</td></tr>
        <tr><td><code>failed</code></td><td>The install or the import check failed. The error text is shown under the badge.</td></tr>
      </tbody>
    </table>

    <h3>How installation works</h3>
    <ul>
      <li>
        The worker checks for claimable entries every 15 seconds by default and
        installs them oldest first. The interval can't be set below 5 seconds.
      </li>
      <li>
        It runs
        <code>pip3 install --target &lt;PackagesDir&gt;/site [--only-binary :all:] &lt;pip_spec&gt;</code>.
        <code>PackagesDir</code> defaults to <code>/app/pyenv</code>. In the Docker
        deployment, that path is the shared <code>python_packages</code> volume.
        Every package goes into the same <code>site</code> directory, and there is
        no per-package isolation.
      </li>
      <li>
        <strong>Wheels only by default</strong>
        (<code>Python:PipOnlyBinary=true</code>, set through
        <code>PYTHON_PIP_ONLY_BINARY</code> in the Docker deployment). pip never
        builds source distributions, so no packaging code runs during install. A
        package with no wheel for the worker's platform fails. An operator can turn
        this off for a whole deployment. It can't be changed per package.
      </li>
      <li>An install that runs longer than 300 seconds by default (<code>Python:PipInstallTimeoutSeconds</code>, minimum 30) is stopped and marked <code>failed</code>.</li>
      <li>
        After a successful install, the worker imports the module once to verify
        it. The package directory is bound <strong>read-only</strong> into the
        snippet sandbox and placed first on <code>PYTHONPATH</code>.
      </li>
      <li>
        Snippet runs read the allow-list from a cache that lasts up to 30 seconds,
        so a change can take that long to reach every worker.
      </li>
    </ul>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Header and warning</h3>
    <p>
      The header has two buttons: <strong>Refresh</strong> and
      <strong>Add module</strong>. Below it, a warning banner reminds you that an
      allowed module is importable by <em>every</em> <code>python_snippet</code>,
      and that installing runs <code>pip install</code> on the worker. The sandbox
      still blocks <code>exec</code>/<code>eval</code>/<code>open</code> and has no
      network unless the snippet is network-enabled.
    </p>

    <h3>Table</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Import name</td><td>The import name. For a pip entry whose spec differs, the spec follows it (for example <code>bs4 · beautifulsoup4</code>).</td></tr>
        <tr><td>Source</td><td>A <code>pip</code> or <code>stdlib</code> badge.</td></tr>
        <tr><td>Status</td><td>Amber for <code>pending</code> and <code>installing</code>, green for <code>ready</code>, red for <code>failed</code>, with the error below (hover to read the full text).</td></tr>
        <tr><td>Installed</td><td>The installed version, <code>stdlib</code> for stdlib entries, or <code>—</code>.</td></tr>
        <tr><td>Actions</td><td><strong>Retry</strong> (only on failed pip entries) and <strong>Remove</strong>.</td></tr>
      </tbody>
    </table>
    <p>
      While any entry is <code>pending</code> or <code>installing</code>, the page
      reloads the list every 3 seconds. Polling stops when you leave the page.
    </p>

    <h3>Add python module dialog</h3>
    <dl>
      <dt>Import name</dt>
      <dd>Required. Placeholder <code>e.g. django, yaml, bs4</code>.</dd>
      <dt>Source</dt>
      <dd>
        <code>pip — install from PyPI</code> (the default) or
        <code>stdlib — already on the interpreter</code>.
      </dd>
      <dt>PyPI package (optional)</dt>
      <dd>
        Shown only for pip. Placeholder
        <code>defaults to the import name — e.g. Django==5.0, beautifulsoup4</code>.
        Set it when the package name differs from the import name, or to pin a
        version.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Main procedure</h2>

    <h3>Allow a pip package</h3>
    <ol>
      <li>Click <strong>Add module</strong>.</li>
      <li>Enter the <strong>Import name</strong> and keep <strong>Source</strong> set to <code>pip</code>.</li>
      <li>If needed, enter the <strong>PyPI package</strong>. Pinning a version is recommended.</li>
      <li>
        Click <strong>Add</strong>. The toast says <em>Added &lt;name&gt;</em> and
        <em>"Installing in the background…"</em>. The row appears as
        <code>pending</code>.
      </li>
      <li>Wait for <code>ready</code>. The page updates by itself.</li>
    </ol>

    <h3>Allow a stdlib module</h3>
    <ol>
      <li>Click <strong>Add module</strong>, enter the module name, and set <strong>Source</strong> to <code>stdlib</code>.</li>
      <li>Click <strong>Add</strong>. The row is <code>ready</code> immediately.</li>
    </ol>

    <h3>Retry a failed install</h3>
    <p>
      Fix the cause first (see <a href="#errors">Errors and recovery</a>), then
      click <strong>Retry</strong> on the row. The entry goes back to
      <code>pending</code> with its error cleared, and the toast says
      <em>"Re-queued for install"</em>. You can't change the spec of an existing
      entry. To use a different spec, remove the entry and add it again.
    </p>

    <h3>Remove a module</h3>
    <ol>
      <li>Click <strong>Remove</strong> on the row.</li>
      <li>Confirm <em>Remove &lt;name&gt;?</em> (<em>"Snippets will no longer be allowed to import it."</em>) with <strong>Remove</strong>.</li>
    </ol>
    <p>
      <strong>Impact:</strong> within about 30 seconds, every snippet that imports
      the module is refused at run time. This includes runs already queued and
      scheduled runs. The entry is deleted permanently, and the audit log keeps the
      only record of it.
    </p>
    <p>
      <strong>To revert:</strong> add the same import name and spec again. The
      installed files are not deleted when you remove an entry (see
      <a href="#limits">Known limitations</a>), but the new entry still goes
      through a fresh <code>pending</code> → <code>ready</code> cycle.
    </p>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Goal:</strong> parse YAML inside a snippet.
    </p>
    <p>
      <strong>Prerequisites:</strong> the <code>admin</code> role and a running
      Linux worker with access to PyPI. The version below is only an example. Pin
      the one you have vetted.
    </p>
    <ol>
      <li>
        Add a module with <strong>Import name</strong> <code>yaml</code>,
        <strong>Source</strong> <code>pip</code>, and <strong>PyPI package</strong>
        <code>PyYAML==&lt;VERSION&gt;</code>.
      </li>
      <li>Wait until the row shows <code>ready</code>, with the version in <em>Installed</em>.</li>
      <li>
        Use it in a <code>python_snippet</code>:
        <pre><code>{`import yaml
from flowweaver_runtime import get_input, set_output

def run(ctx):
    doc = yaml.safe_load(get_input()["text"])
    set_output({"keys": sorted(doc.keys())})`}</code></pre>
      </li>
    </ol>
    <p>
      <strong>Expected result:</strong> the step succeeds and outputs the
      top-level keys.
    </p>
    <p>
      <strong>Common error:</strong> the step fails with
      <em>Blocked: … imports disallowed module 'yaml'</em>. The entry is still
      <code>pending</code> or <code>installing</code>, it <code>failed</code>, or it
      became <code>ready</code> less than 30 seconds ago.
      <strong>Recovery:</strong> wait for <code>ready</code> (plus up to 30
      seconds), or fix and retry the failed entry, then run the step again.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li>Only users with the <code>admin</code> role can list, add, retry or remove entries.</li>
      <li>
        Adding, removing and retrying are written to the audit log (entity
        <code>allowed_python_module</code>, actions <code>create</code>,
        <code>delete</code>, <code>retry_install</code>), including the exact
        <code>pip_spec</code>. See <a href="/docs/admin/audit">Audit</a>.
      </li>
      <li>
        The allow-list only decides which modules can be imported. Even for allowed
        modules, the static check still refuses <code>eval</code>,
        <code>exec</code>, <code>compile</code>, <code>__import__</code>,
        <code>open</code>, <code>getattr</code>/<code>setattr</code>/<code>delattr</code>,
        <code>vars</code>, <code>globals</code>, <code>locals</code>,
        <code>input</code>, <code>breakpoint</code>, <code>memoryview</code>,
        interpreter internals such as <code>__builtins__</code> and
        <code>__class__</code>, and relative imports. The sandbox stays
        read-only and has no network.
      </li>
    </ul>
    <Callout tone="warning" title="Precautions">
      <ul>
        <li>An allowed module is available to <strong>every</strong> snippet, not just the one that needed it.</li>
        <li>
          A package runs its own code inside every snippet that imports it. Add
          only packages you trust, pin exact versions, and keep wheels-only
          installs on.
        </li>
        <li>
          Don't use a <code>stdlib</code> entry to allow <code>os</code>,
          <code>subprocess</code>, <code>socket</code> or similar modules. The server
          doesn't stop you, and such modules defeat the purpose of the import
          check.
        </li>
        <li>
          The worker process that runs pip has network access. Only the snippet
          sandbox is isolated.
        </li>
      </ul>
    </Callout>

    <h3>Difference from network-enabled snippets</h3>
    <table>
      <thead><tr><th></th><th>Python packages (this page)</th><th>Network-enabled snippet</th></tr></thead>
      <tbody>
        <tr><td>Scope</td><td>Every <code>python_snippet</code></td><td>One snippet</td></tr>
        <tr><td>Changes</td><td>Which modules can be imported</td><td>Grants host network and DNS, and allows the netmiko/paramiko/socket module set</td></tr>
        <tr><td>Network</td><td>Still none</td><td>Host network</td></tr>
        <tr><td>Where to set it</td><td><code>/admin/python-packages</code></td><td>Snippet editor or node <em>Service</em> tab (admin only)</td></tr>
      </tbody>
    </table>
    <p>
      Installing <code>requests</code> here doesn't let a snippet reach the network.
      For that, use <code>flowweaver_runtime</code>'s integration helper or a
      <a href="/docs/snippets#network-enabled">network-enabled</a> snippet.
    </p>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <dl>
      <dt>Loading</dt><dd>A spinner labelled <em>"Loading modules…"</em>.</dd>
      <dt>Load error</dt><dd>An error card with a retry button.</dd>
      <dt>No entries</dt>
      <dd>
        <em>"No extra modules yet"</em>, with the hint <em>"The built-in stdlib
        safe-list is always available. Add a module to allow more imports."</em>
        and an <strong>Add module</strong> button. The built-in modules card is
        still shown.
      </dd>
    </dl>
  </section>

  <section id="errors">
    <h2>Errors and recovery</h2>

    <h3>When adding</h3>
    <table>
      <thead><tr><th>Message</th><th>Cause and recovery</th></tr></thead>
      <tbody>
        <tr><td><em>Import name is required</em></td><td>The field is empty. Fill it in.</td></tr>
        <tr><td><em>import_name must be a top-level Python module identifier (letters, digits, underscore; no dots)…</em></td><td>A stdlib entry has a dotted or invalid name (code <code>import_name_invalid</code>). Use only the top-level name.</td></tr>
        <tr><td><em>import_name must be a package name (letters, digits and . _ -) or a top-level Python module identifier.</em></td><td>A pip entry has invalid characters. Remove spaces and symbols.</td></tr>
        <tr><td><em>pip_spec contains invalid characters (allowed: letters, digits, . _ - [ ] = &lt; &gt; ! ~ ,).</em></td><td>Code <code>pip_spec_invalid</code>. Remove spaces, quotes, or anything that looks like a URL or path.</td></tr>
        <tr><td><em>module '&lt;name&gt;' is already on the allow-list.</em></td><td>Code <code>import_name_duplicate</code>. The entry already exists. Retry it, or remove it first.</td></tr>
      </tbody>
    </table>

    <h3>When installing (shown under a failed badge)</h3>
    <table>
      <thead><tr><th>Error</th><th>Cause and recovery</th></tr></thead>
      <tbody>
        <tr><td><em>pip install failed (exit N): …</em></td><td>pip failed. Usual causes: the name or version doesn't exist, there is no wheel for the platform (wheels-only), or the worker can't reach PyPI. Read the pip output, then remove and re-add with a corrected spec, or retry once the cause is fixed.</td></tr>
        <tr><td><em>timed out after Ns</em></td><td>The install took longer than the timeout. Retry, or ask an operator to raise <code>Python:PipInstallTimeoutSeconds</code>.</td></tr>
        <tr><td><em>installed but '&lt;name&gt;' did not import — check the import name vs the pip package.</em></td><td>The import name doesn't match what the package provides. Remove the entry and re-add it with the right import name.</td></tr>
        <tr><td><em>'&lt;spec&gt;' installed, but the module it provides could not be determined — set the import name by hand…</em></td><td>You typed a package name, and discovery found no module. Re-add the entry with the real import name.</td></tr>
        <tr><td><em>'&lt;spec&gt;' imports as '&lt;name&gt;', which is already allowed — remove one of the two rows</em></td><td>Two entries provide the same module. Remove one.</td></tr>
      </tbody>
    </table>
    <p>
      <strong>Stuck in <code>pending</code>:</strong> no worker is installing.
      Installation only runs in a worker-mode process (<code>WorkerOnly</code> /
      <code>--worker</code>) on Linux, and it is disabled on other hosts. Check that
      the worker is running and look in its log for
      <code>PythonPackageProvisioner started</code>.
    </p>

    <h3>At snippet run time</h3>
    <ul>
      <li>
        <em>Blocked: script imports disallowed module '&lt;name&gt;'</em> or
        <em>Blocked: line N: imports disallowed module '&lt;name&gt;'</em>: the
        module isn't a <code>ready</code> entry. The check runs when the step
        executes, not when the snippet is saved.
      </li>
      <li>
        A <code>ModuleNotFoundError</code> from the script with a
        <code>ready</code> stdlib entry: the module doesn't exist on the
        interpreter, most likely because of a typo.
      </li>
    </ul>
  </section>

  <section id="limits">
    <h2>Known limitations</h2>
    <ul>
      <li>
        <strong>Removing an entry doesn't uninstall the package.</strong> Its files
        stay in the shared <code>site</code> directory. Nothing cleans them up
        today, although a comment in the server code says a cleanup sweep does.
        The module still can't be imported once the entry is gone.
      </li>
      <li>You can't edit an entry. To change the spec or version, remove it and add it again.</li>
      <li>Retry is only offered for failed pip entries. An install can't be cancelled.</li>
      <li>All packages share one directory. FlowWeaver doesn't manage conflicts between the dependencies of different packages.</li>
      <li>Only top-level names are checked. Allowing a package allows all of its submodules.</li>
      <li>Wheels-only mode applies to the whole deployment and can't be switched per package.</li>
      <li>On non-Linux development hosts, pip entries never leave <code>pending</code>.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/snippets#extra-imports">Snippets → Extra imports &amp; packages</a></li>
      <li><a href="/docs/snippets#network-enabled">Snippets → Network-enabled</a></li>
      <li><a href="/docs/admin/secrets">Secrets</a>, for passing credentials to a snippet without putting them in code.</li>
      <li><a href="/docs/admin/audit">Audit</a>, for the history of allow-list changes.</li>
    </ul>
  </section>
</DocLayout>
