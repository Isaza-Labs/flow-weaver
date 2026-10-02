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
      Only three paths are public — <code>/login</code>, <code>/logout</code> and
      <code>/link</code> (account linking from a chat channel). Every other URL
      redirects to the sign-in screen when no valid session token is present.
      Deep links are preserved: if you tried to open <code>/runs/abc</code> while
      logged out, the login URL becomes <code>/login?redirect=/runs/abc</code> and
      you'll be forwarded to that destination after a successful sign-in.
    </p>

    <h3>Fields</h3>
    <dl>
      <dt>Username</dt>
      <dd>
        Your account name. It is what identifies you — the sign-in screen
        asks for nothing else beyond your password.
      </dd>
      <dt>Password</dt>
      <dd>Must satisfy the server's password policy: by default at least 12 characters, with upper- and lower-case letters, a digit and a symbol. Change it from the user menu (see below).</dd>
    </dl>

    <Callout tone="info" title="Forgot password?">
      There is no password-reset flow: not self-service, and not from the
      Users screen, which sets a password only when it creates an account.
      Contact your FlowWeaver administrator.
    </Callout>
  </section>

  <section>
    <h2>The app shell</h2>
    <p>
      Every authenticated page shares the same layout. Understanding its regions
      up-front saves you a lot of hunting later.
    </p>

    <h3>Left sidebar</h3>
    <p>
      240 px wide, collapsible to a 56 px icon rail. Navigation is grouped by
      activity, and entries your role doesn't reach are simply absent:
    </p>
    <ul>
      <li><strong>Dashboard</strong> — the home landing page at <code>/</code>.</li>
      <li>
        <strong>Intelligence</strong> — <em>AI</em>. Deliberately the first group after the
        dashboard and the only <em>featured</em> entry (tinted and ringed rather than plain),
        because the assistant is the entry point to most of what follows.
      </li>
      <li><strong>Build</strong> — Workflows, Subflows, Snippets, Git repos.</li>
      <li>
        <strong>Integrate</strong> — everything that connects FlowWeaver to a system outside
        it: Integrations, and — admin only — Vendor commands, MCP servers, Messaging
        channels, Email.
      </li>
      <li>
        <strong>Operate</strong> — Runs, Schedules, Devices, Device pools, Credentials,
        QA lab, and Artifacts (admin only).
      </li>
      <li>
        <strong>Govern</strong> — Policies, Permissions, Python packages. The whole group is
        admin only.
      </li>
      <li><strong>Help</strong> — Documentation (this manual).</li>
    </ul>
    <p>
      The active entry shows a primary-color accent bar on its left edge and a
      faint ring on its background pill. Matching is longest-prefix, so
      <code>/workflows/abc</code> still highlights <em>Workflows</em>.
    </p>

    <h3>User row (bottom of the sidebar)</h3>
    <p>
      Shows your initial in a circular avatar, your username, and your role
      (<code>admin</code>, <code>operator</code>, or <code>viewer</code>). Clicking
      the avatar opens a menu — arrow keys move between items, <kbd>Esc</kbd> closes it:
    </p>
    <ul>
      <li><strong>Admin dashboard</strong> — only present for admins. Links to <code>/admin</code>.</li>
      <li><strong>Change password</strong> — links to <a href="/settings/password"><code>/settings/password</code></a>.</li>
      <li><strong>Sign out</strong> — clears your session and sends you back to <code>/login</code>.</li>
    </ul>

    <h3>Footer</h3>
    <p>Holds three small controls and a version stamp:</p>
    <ul>
      <li>
        <strong>Help mascot toggle</strong> — a question-mark button that re-enables the
        floating guide mascot. It only appears once you've dismissed the mascot, so it
        isn't offering you something you already have.
      </li>
      <li>
        <strong>Theme toggle</strong> — switches between light and dark mode. Your
        preference is stored locally.
      </li>
      <li>
        <strong>Collapse sidebar</strong> — shrinks the sidebar to the icon rail. Desktop
        only, and remembered across sessions.
      </li>
      <li><strong>Version</strong> — the running frontend version (e.g. <code>v0.1.0</code>).</li>
    </ul>

    <h3>Main content area</h3>
    <p>
      Everything to the right of the sidebar. Most pages open with a
      <strong>PageHeader</strong> (title + short description + a row of primary actions)
      and continue with cards, tables, or editors.
    </p>

    <h3>On a phone</h3>
    <p>
      Below the <code>md</code> breakpoint the sidebar becomes an off-canvas drawer:
      a top bar appears with a hamburger button and the wordmark, and the drawer slides
      in over a dimmed backdrop. <kbd>Esc</kbd>, the backdrop, or following any link
      closes it. The desktop collapse preference is kept separate, so closing the
      drawer on a phone doesn't shrink your sidebar back on a laptop.
    </p>

    <h3>Connection banner</h3>
    <p>
      A strip above the layout that appears when the frontend loses contact with the
      backend. If pages look empty or actions silently fail, check for it before
      assuming your data is gone.
    </p>
  </section>

  <section>
    <h2>The command palette</h2>
    <p>
      Press <kbd>Ctrl</kbd>/<kbd>⌘</kbd> + <kbd>K</kbd> from anywhere — or just
      <kbd>/</kbd> when your cursor isn't in a text field — to open a searchable
      list of everything you can do without leaving the keyboard. Entries are
      grouped:
    </p>
    <ul>
      <li>
        <strong>Navigate</strong> — every top-level destination: Dashboard, Workflows,
        Subflows, Snippets, Integrations, Runs, Schedules, Devices, Device pools,
        Credentials, QA lab, AI, Documentation.
      </li>
      <li><strong>Actions</strong> — <em>New workflow</em>, which opens the create form directly.</li>
      <li>
        <strong>Appearance</strong> — toggle light/dark, collapse or expand the sidebar,
        and switch table density between compact and comfortable.
      </li>
      <li>
        <strong>Admin</strong> — Admin dashboard, Artifacts, Policies. Hidden for
        non-admins.
      </li>
    </ul>

    <Callout tone="success" title="Tip">
      The palette is the fastest way to reach an admin page that isn't in your
      sidebar group, and the only place the <strong>table density</strong> preference is
      exposed.
    </Callout>
  </section>

  <section>
    <h2>Roles and what each one sees</h2>
    <p>
      FlowWeaver has three built-in roles. Your role is attached to your session
      token when you sign in and controls two things: which sidebar entries
      appear, and which server-side endpoints accept your calls.
    </p>
    <p>
      An admin can refine this with <a href="/docs/permissions">granular
      permissions</a>: capability grants assigned to users, optionally limited to
      an environment, device or resource, plus per-workflow and per-integration
      access. The table below is the baseline; see
      <a href="/docs/reference">Reference</a> for the full role × action matrix.
    </p>

    <table>
      <thead>
        <tr><th>Role</th><th>Purpose</th><th>Sees</th></tr>
      </thead>
      <tbody>
        <tr>
          <td><code>viewer</code></td>
          <td>Read-only observer. Can browse runs, schedules, the QA lab, and chat with the AI.</td>
          <td>Dashboard, Intelligence, Build (read-only), Integrate (Integrations only), Operate (read-only), Help.</td>
        </tr>
        <tr>
          <td><code>operator</code></td>
          <td>Day-to-day builder. Can create/edit/delete workflows, subflows, snippets, integrations, devices, pools, and credentials.</td>
          <td>Same as viewer plus write access on Workflows, Subflows, Snippets, Integrations, Schedules, Devices, Device pools and Credentials, and on Vendor commands by URL. Git write operations, MCP servers, messaging and email channel configuration, and Artifacts stay admin-only.</td>
        </tr>
        <tr>
          <td><code>admin</code></td>
          <td>Full control. Bulk-deletes, role changes, policy authoring, secrets, audit inspection.</td>
          <td>Everything, plus the <strong>Govern</strong> group, the admin-only entries under Integrate and Operate, and the <strong>Admin dashboard</strong> in the user menu.</td>
        </tr>
      </tbody>
    </table>

    <Callout tone="admin" title="Hidden is not the same as blocked">
      The browser redirects non-admins away
      from <code>/admin/*</code> only. The other admin-only screens —
      <code>/policies</code>, <code>/permissions</code>, <code>/vendor-commands</code>,
      <code>/email</code> — are hidden from the sidebar rather than guarded in the
      browser; deep-linking to one loads the page and the server decides what its
      calls may do. Of those four, only <code>/permissions</code> is genuinely
      admin-only. The other three are admin-only in the sidebar, not on the
      server: reading the vendor-command catalog, the corporate policies
      (<code>/policies</code> and <code>/policies/audit</code>) and the email
      channel list are all viewer-level capabilities, so a viewer who deep-links
      to one gets a working page with real data — only the write actions on it
      (authoring a policy, configuring a channel) are refused. Operators can go
      further and manage catalog entries under <code>/vendor-commands</code>.
      The role check that matters happens on the server, every time.
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
    <h2>Your first workflow in six steps</h2>
    <ol>
      <li>
        <strong>Register at least one device.</strong> Open
        <a href="/devices"><code>/devices</code></a>, click <em>New device</em>, and
        fill in <em>Name</em> and <em>IP address</em>. <em>Platform</em> and a
        <em>Credential</em> are not needed for a ping, but you will want them as
        soon as you log in to the device (create the credential from
        <a href="/credentials"><code>/credentials</code></a> first). Make sure the
        hammer (<em>draft</em>) icon in the device's <em>Environments</em> column is
        lit, or a draft run will not reach it.
      </li>
      <li>
        <strong>Use the built-in <code>ping</code> snippet.</strong> It ships with
        FlowWeaver, needs no code and no credential, and runs once per target
        device. You will find it in the editor palette in the next step — no need
        to create anything in <a href="/snippets"><code>/snippets</code></a>.
      </li>
      <li>
        <strong>Create a workflow.</strong> From
        <a href="/workflows"><code>/workflows</code></a>, click <em>New workflow</em>,
        type a name and submit. The new workflow appears in the <em>Draft</em> list;
        click its name to open the DAG editor, which starts with a
        <code>__start__</code> and an <code>__end__</code> sentinel node. Drag
        <code>ping</code> from the palette's <em>Snippets</em> group onto the canvas
        (on a fresh install it sits in the collapsed <em>Unproven</em> section —
        typing <code>ping</code> in the search box expands it)
        and connect <code>__start__ → your_node → __end__</code>. Save with the
        <em>Save</em> button or <kbd>Ctrl</kbd>/<kbd>⌘</kbd> + <kbd>S</kbd>.
      </li>
      <li>
        <strong>Simulate before you run.</strong> The editor's <em>Simulate</em> button
        walks the graph without touching a device and reports ordering problems,
        unresolved templates and missing configuration. Cheaper than finding out on
        real hardware.
      </li>
      <li>
        <strong>Run it.</strong> Click <em>Run</em>. The dialog collects the runtime
        input — rendered from the workflow's input schema, so you only see fields it
        actually declares — and the target devices, with the picker scoped to the
        workflow's environment. If no node in the graph runs per device, targets are
        optional and the workflow executes once without a device context.
      </li>
      <li>
        <strong>Inspect the monitor.</strong> The DAG highlights each node with its
        live status; the right-hand timeline lists every step as it executes with
        logs, input, and output. If a node fails, use <em>Fix with AI</em> to
        hand the context to the assistant. The eye icon in the editor toolbar
        re-opens the last run's data without leaving the canvas. A successful
        ping step shows as green; its output carries the reachability result.
      </li>
    </ol>

    <Callout tone="info" title="If the run is refused">
      <code>409 no runnable targets in environment 'draft'</code> means every
      device you picked has the <em>draft</em> environment switched off. Light the
      hammer icon on the device row in <a href="/devices"><code>/devices</code></a>
      and run again.
    </Callout>

    <h3>Next: log in to the device</h3>
    <p>
      Once the ping works, swap the node for the built-in <code>ssh</code> snippet
      to run CLI commands. It needs the device's <em>Platform</em> and an attached
      <em>Credential</em>, and each node sets <code>command</code> (or a
      <code>commands</code> list) in its config. Writing your own
      <code>python_snippet</code> with Netmiko or paramiko is the advanced path — see
      <a href="/docs/snippets">Snippets</a>.
    </p>

    <Callout tone="success" title="Tip">
      Drafts are safe to iterate on — they never fan out to production devices.
      When you're confident, use the <strong>Promote</strong> action on the
      workflow to move it to QA, run it once against the QA lab, and then promote
      again to production.
    </Callout>

    <Callout tone="warning" title="Leaving with unsaved changes">
      The editor tracks a dirty flag. Navigating away — or closing the browser tab —
      with unsaved graph changes raises a confirmation first. Choosing
      <em>Leave anyway</em> discards them.
    </Callout>
  </section>

  <section>
    <h2>URL cheat sheet</h2>
    <dl>
      <dt>/</dt>
      <dd>Dashboard.</dd>
      <dt>/workflows</dt>
      <dd>
        List of workflows. <code>/workflows/{'{id}'}</code> opens the DAG editor;
        <code>?new=1</code> opens the create form directly.
      </dd>
      <dt>/subflows</dt>
      <dd>Reusable sub-graphs, callable as a node from another workflow.</dd>
      <dt>/runs</dt>
      <dd>
        Run history. <code>/runs/{'{id}'}</code> is the detail page and
        <code>/runs/{'{id}'}/monitor</code> the live graph view.
      </dd>
      <dt>/qa</dt>
      <dd>QA lab — promotion readiness and QA-scoped runs.</dd>
      <dt>/ai/chat</dt>
      <dd>
        Full assistant chat. Accepts <code>?q=</code> to preload a prompt,
        <code>?context=</code> to scope the context, or <code>?fix=</code> with a
        JSON blob to auto-start a diagnosis.
      </dd>
      <dt>/settings/password</dt>
      <dd>Change your own password.</dd>
      <dt>/policies</dt>
      <dd>Guardrails and approval gates. Admins author them; every role can read them by opening the URL.</dd>
      <dt>/permissions</dt>
      <dd>Granular capability grants assigned to users, optionally scoped to an environment, device or resource. Admin only. See <a href="/docs/permissions">Permissions</a>.</dd>
      <dt>/admin</dt>
      <dd>
        Admin dashboard. Children include <code>/admin/users</code>,
        <code>/admin/audit</code>, <code>/admin/traces</code>,
        <code>/admin/artifacts</code>, <code>/admin/secrets</code>,
        <code>/admin/mcp-servers</code>, <code>/admin/messaging-channels</code>,
        <code>/admin/python-packages</code>, <code>/admin/slo</code>,
        <code>/admin/settings</code>, <code>/admin/actions</code>. The
        <a href="/docs/reference">Reference</a> page lists every screen with its
        route, minimum role and chapter.
      </dd>
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
