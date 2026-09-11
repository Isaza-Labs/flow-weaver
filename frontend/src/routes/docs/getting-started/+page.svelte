<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Getting started"
  lead="Sign in, learn the app shell, understand how roles shape what you see, and run your first workflow."
>
  <Callout tone="where" title="Where to find it">
    Sign-in: <a href="/login"><code>/login</code></a>. Once authenticated you land on the
    <a href="/">dashboard</a> at <code>/</code>.
  </Callout>

  <section>
    <h2>Signing in</h2>
    <p>
      The sign-in screen is the only public page in the application — every other
      URL redirects here when no valid session token is present. Deep links are
      preserved: if you tried to open <code>/runs/abc</code> while logged out, the
      login URL becomes <code>/login?redirect=/runs/abc</code> and you'll be
      forwarded to that destination after a successful sign-in.
    </p>

    <h3>Fields</h3>
    <dl>
      <dt>Username</dt>
      <dd>
        Your account name. It is what identifies you — the sign-in screen
        asks for nothing else beyond your password.
      </dd>
      <dt>Password</dt>
      <dd>Minimum 8 characters. Set or change it from the user menu (see below).</dd>
    </dl>

    <Callout tone="info" title="Forgot password?">
      There is no self-service reset flow. Ask an admin to set a new
      password for you from <a href="/admin/users"><code>/admin/users</code></a>.
    </Callout>
  </section>

  <section>
    <h2>The app shell</h2>
    <p>
      Every authenticated page shares the same layout. Understanding the three
      regions up-front saves you a lot of hunting later.
    </p>

    <h3>Left sidebar (60 px wide)</h3>
    <p>
      Primary navigation, grouped by activity:
    </p>
    <ul>
      <li><strong>Dashboard</strong> — the home landing page.</li>
      <li><strong>Build</strong> — Workflows, Snippets, Integrations.</li>
      <li><strong>Operate</strong> — Runs, Schedules, Devices, Device pools, Credentials, QA lab.</li>
      <li><strong>Govern</strong> — Policies. Hidden for non-admin users.</li>
      <li><strong>Intelligence</strong> — AI hub and all its sub-pages.</li>
    </ul>
    <p>
      The active section shows a primary-colour accent bar on its left edge and a
      faint ring on its background pill.
    </p>

    <h3>User row (bottom of the sidebar)</h3>
    <p>
      Shows your initial in a circular avatar, your username, and your role
      (<code>admin</code>, <code>operator</code>, or <code>viewer</code>). Clicking
      the avatar opens a menu with:
    </p>
    <ul>
      <li><strong>Admin dashboard</strong> — only present for admins. Links to <code>/admin</code>.</li>
      <li><strong>Change password</strong> — links to <a href="/settings/password"><code>/settings/password</code></a>.</li>
      <li><strong>Sign out</strong> — clears your session and sends you back to <code>/login</code>.</li>
    </ul>

    <h3>Footer</h3>
    <p>Holds two small controls and a version stamp:</p>
    <ul>
      <li>
        <strong>Help mascot toggle</strong> — a question-mark button that re-enables the
        floating guide mascot once you've dismissed it. The mascot surfaces
        context-aware tips about the page you're on.
      </li>
      <li>
        <strong>Theme toggle</strong> — switches between light and dark mode. Your
        preference is stored locally.
      </li>
      <li><strong>Version</strong> — the running frontend version (e.g. <code>v0.1.0</code>).</li>
    </ul>

    <h3>Main content area</h3>
    <p>
      Everything to the right of the sidebar. Most pages open with a
      <strong>PageHeader</strong> (title + short description + a row of primary actions)
      and continue with cards, tables, or editors.
    </p>
  </section>

  <section>
    <h2>Roles and what each one sees</h2>
    <p>
      FlowWeaver has three built-in roles. Your role is attached to your session
      token when you sign in and controls two things: which sidebar entries
      appear, and which server-side endpoints accept your calls.
    </p>

    <table>
      <thead>
        <tr><th>Role</th><th>Purpose</th><th>Sees</th></tr>
      </thead>
      <tbody>
        <tr>
          <td><code>viewer</code></td>
          <td>Read-only observer. Can browse runs, schedules, the QA lab, and chat with the AI.</td>
          <td>Dashboard, Build (read-only), Operate (read-only), Intelligence.</td>
        </tr>
        <tr>
          <td><code>operator</code></td>
          <td>Day-to-day builder. Can create/edit/delete workflows, snippets, integrations, devices, pools, and credentials.</td>
          <td>Same as viewer plus write access to all Build and Operate screens.</td>
        </tr>
        <tr>
          <td><code>admin</code></td>
          <td>Full control. Bulk-deletes, role changes, policy authoring, audit inspection.</td>
          <td>Everything, plus the <strong>Govern</strong> group in the sidebar and the <strong>Admin dashboard</strong> entry in the user menu.</td>
        </tr>
      </tbody>
    </table>

    <Callout tone="admin" title="Admin-only routes">
      Every <code>/admin/*</code> path and <code>/policies</code> require the
      <code>admin</code> role. Non-admins who deep-link to those URLs are bounced
      back to the dashboard by the client-side guard in <code>+layout.svelte</code>.
    </Callout>
  </section>

  <section>
    <h2>The guide mascot</h2>
    <p>
      A floating helper that watches the route you're on and offers tailored tips
      in a small popover. Click it to get contextual hints, or use its
      <strong>Open full chat</strong> action to jump into the AI chat with the
      current route pre-loaded as context.
    </p>
    <p>
      Dismiss it with the <kbd>×</kbd> button — the preference is remembered. To
      bring it back, click the <kbd>?</kbd> button in the sidebar footer.
    </p>
  </section>

  <section>
    <h2>Your first workflow in five steps</h2>
    <ol>
      <li>
        <strong>Register at least one device.</strong> Open
        <a href="/devices"><code>/devices</code></a>, click <em>New device</em>, and
        fill in <em>Name</em>, <em>IP address</em>, <em>Platform</em>, and attach a
        <em>Credential</em> (create one from
        <a href="/credentials"><code>/credentials</code></a> first).
      </li>
      <li>
        <strong>Pick or create a snippet.</strong> Browse
        <a href="/snippets"><code>/snippets</code></a>. For an SSH command, a
        <em>python_snippet</em> with <code>netmiko</code> is a safe default, or use a
        built-in <em>ping</em> snippet to start.
      </li>
      <li>
        <strong>Create a workflow.</strong> From
        <a href="/workflows"><code>/workflows</code></a>, click <em>New workflow</em>.
        The DAG editor opens with a <code>__start__</code> and <code>__end__</code>
        sentinel node. Drag your snippet into the canvas between them and connect
        <code>__start__ → your_node → __end__</code>.
      </li>
      <li>
        <strong>Run it.</strong> From the editor's <em>Run</em> button, select one
        or more target devices (or a pool), provide the input payload if the
        snippet requires one, and submit. You are redirected to the run monitor.
      </li>
      <li>
        <strong>Inspect the monitor.</strong> The DAG highlights each node with its
        live status; the right-hand timeline lists every step as it executes with
        logs, input, and output. If a node fails, use <em>Fix with AI</em> to
        hand the context to the assistant.
      </li>
    </ol>

    <Callout tone="success" title="Tip">
      Drafts are safe to iterate on — they never fan out to production devices.
      When you're confident, use the <strong>Promote</strong> action on the
      workflow to move it to QA, run it once against the QA lab, and then promote
      again to production.
    </Callout>
  </section>

  <section>
    <h2>URL cheat sheet</h2>
    <dl>
      <dt>/</dt>
      <dd>Dashboard.</dd>
      <dt>/workflows</dt>
      <dd>List of workflows. <code>/workflows/{'{id}'}</code> opens the DAG editor.</dd>
      <dt>/runs</dt>
      <dd>Run history. <code>/runs/{'{id}'}/monitor</code> is the live graph view.</dd>
      <dt>/ai/chat</dt>
      <dd>
        Full assistant chat. Accepts <code>?q=</code> to preload a prompt,
        <code>?context=</code> to scope the context, or <code>?fix=</code> with a
        JSON blob to auto-start a diagnosis.
      </dd>
      <dt>/admin</dt>
      <dd>Admin dashboard (admin only). Children: <code>/admin/users</code>, <code>/admin/audit</code>, <code>/admin/traces</code>, <code>/admin/artifacts</code>.</dd>
      <dt>/docs</dt>
      <dd>This manual.</dd>
    </dl>
  </section>

  <section>
    <h2>What's next</h2>
    <p>
      Head to the <a href="/docs/dashboard">Dashboard</a> page to learn what the
      home screen shows, or jump directly into the <a href="/docs/workflows">Workflows</a>
      chapter.
    </p>
  </section>
</DocLayout>
