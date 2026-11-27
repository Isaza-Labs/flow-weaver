<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Python environments"
  lead="Separate Python virtualenvs, each with its own pinned packages, built on the worker at runtime with uv. SSH devices, Python snippets and Ansible playbooks can each pick one instead of the image's system python3."
>
  <Callout tone="where" title="Where to find it">
    Sidebar → <strong>Govern</strong> → <strong>Python</strong>, <strong>Environments</strong> tab
    (<a href="/admin/python?tab=environments"><code>/admin/python?tab=environments</code></a>).
  </Callout>
  <Callout tone="admin" title="Admin only">
    Creating, editing, rebuilding and deleting environments requires the
    <code>admin</code> role: building one installs third-party packages, which runs
    their packaging code on the worker. Anyone who can edit devices or snippets can
    read the list, so the pickers work. Credentials in index URLs are masked.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      The image ships one Python with one set of packages. Sometimes a device needs
      a different version of a library than everything else. The motivating case:
      paramiko 5 removed the <code>ssh-rsa</code> host key algorithm, and some
      switches (for example Extreme EXOS running OpenSSH 8.1) offer nothing else, so
      every SSH step to them fails with
      <code>Incompatible ssh peer (no acceptable host key)</code>. Rather than
      downgrading paramiko for the whole platform, an environment pins the older
      version only where it is needed.
    </p>
  </section>

  <section>
    <h2>How it works</h2>
    <ul>
      <li>
        Each environment has a <strong>name</strong>, an optional
        <strong>Python version</strong> (empty = the image's <code>python3</code>)
        and <strong>requirements</strong> in <code>requirements.txt</code> format.
      </li>
      <li>
        The worker builds it with <code>uv venv</code> + <code>uv pip install</code>
        into a temporary directory and swaps it into place only when the build
        succeeded. A running step never sees a half-built environment, and a failed
        rebuild keeps the previous good build.
      </li>
      <li>
        Status goes <code>pending</code> → <code>building</code> →
        <code>ready</code> or <code>failed</code>. Editing the requirements or the
        Python version, or pressing <strong>Rebuild</strong>, sets it back to
        <code>pending</code>. After a build the page shows the resolved package
        list (<code>uv pip freeze</code>).
      </li>
      <li>
        Leaving the environment empty anywhere means the <strong>system default</strong>
        — exactly the behaviour from before environments existed.
      </li>
    </ul>
  </section>

  <section>
    <h2>Where an environment is chosen</h2>
    <ul>
      <li>
        <strong>Device</strong> — the SSH runner for that device runs with the
        environment's interpreter.
      </li>
      <li>
        <strong>Python snippet</strong> — the script runs with the environment's
        interpreter. Every sandbox rule still applies: the import allow-list
        (<a href="/docs/admin/python-packages">Python packages</a>), the isolation and
        the timeouts. An environment changes which version of an allowed module is
        imported, never which modules are allowed.
      </li>
      <li>
        <strong>Ansible playbook</strong> — <code>ansible-playbook</code> runs from
        the environment's <code>bin</code> directory, so the environment must list
        <code>ansible-core</code> (or <code>ansible</code>) in its requirements.
      </li>
    </ul>
    <p>
      An environment that is not <code>ready</code> makes the step fail with a clear
      error; it is never silently replaced by another interpreter.
    </p>
  </section>

  <section>
    <h2>SSH fallback and <code>ssh-legacy</code></h2>
    <p>
      One environment can be flagged as the <strong>SSH fallback</strong>. When an
      SSH step fails with the paramiko "no acceptable host key" error, the runner
      retries once with that environment and records it in the step output
      (<code>python_environment</code>, <code>ssh_env_fallback</code>).
    </p>
    <p>
      On the first start with no environments defined (new or upgraded installation) an environment named <code>ssh-legacy</code> is
      created and flagged as the fallback, so these devices work out of the box:
    </p>
    <pre><code>netmiko&gt;=4,&lt;5
paramiko&gt;=3.5,&lt;5
textfsm&gt;=1.1
ntc-templates&gt;=4</code></pre>
  </section>
</DocLayout>
