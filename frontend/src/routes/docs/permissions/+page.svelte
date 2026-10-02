<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Permissions"
  lead="Decide who can do what in FlowWeaver: base roles, capability grants with optional conditions, and per-resource roles on individual workflows and integrations."
>
  <Callout tone="where" title="Where to find it">
    Capability grants: <a href="/permissions"><code>/permissions</code></a> —
    Sidebar → <strong>Govern</strong> → <strong>Permissions</strong>.<br />
    Workflow grants: <code>/workflows/&lt;workflow-id&gt;/permissions</code> — open a
    workflow in the builder, click <strong>More</strong> in the toolbar, then
    <strong>Permissions</strong>.<br />
    Integration grants: <code>/integrations/&lt;integration-id&gt;/permissions</code> —
    on <a href="/integrations"><code>/integrations</code></a>, expand an integration
    card and click <strong>Permissions</strong>.
  </Callout>
  <Callout tone="admin" title="Admin only (mostly)">
    Every call behind <code>/permissions</code> requires the <code>admin</code> role,
    and the sidebar entry is only shown to admins. The per-workflow and
    per-integration pages can be opened by any signed-in user, but only admins see
    the grant form and the <em>Revoke</em> buttons.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      FlowWeaver authorizes every request in layers. This chapter explains each
      layer, how they combine, and how to use them without locking anyone out:
    </p>
    <ol>
      <li><strong>Base role</strong> — every account has exactly one of <code>admin</code>, <code>operator</code> or <code>viewer</code> (set under <a href="/docs/admin/users">Users</a>).</li>
      <li><strong>Capability grants</strong> — named bundles of capabilities (for example <code>workflow.run</code>) assigned to users, optionally limited by conditions. They decide access only when the RBAC mode is <code>granular</code>.</li>
      <li><strong>Per-resource roles</strong> — <code>owner</code>, <code>editor</code>, <code>runner</code> or <code>viewer</code> on one workflow or integration. They are checked only when the per-resource gating toggle is on.</li>
    </ol>
    <p>
      The two switches that turn layers 2 and 3 on live in
      <a href="/docs/admin/settings">Application settings</a>.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>You need the <code>admin</code> role to create grants or assign per-resource roles.</li>
      <li>The users you want to grant must already exist (<a href="/admin/users"><code>/admin/users</code></a>). The pickers list existing accounts only.</li>
      <li>Check the current <strong>RBAC mode</strong> and the <strong>per-resource grants</strong> toggle in <a href="/admin/settings"><code>/admin/settings</code></a>. Grants you create while the mode is <code>legacy</code> are stored but have no effect until you switch to <code>granular</code>.</li>
      <li>To scope a grant to devices or device pools, the devices and pools must exist; the condition builder only offers existing values.</li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>

    <h3>Base roles</h3>
    <dl>
      <dt>admin</dt>
      <dd>Bypasses every capability check and every per-resource check. An admin can always reach <code>/permissions</code>, <code>/admin/settings</code> and <code>/admin/users</code>, whatever the settings say.</dd>
      <dt>operator</dt>
      <dd>Day-to-day builder. In <code>legacy</code> mode gets every capability of the Viewer and Operator tiers (see the matrix below).</dd>
      <dt>viewer</dt>
      <dd>Read-mostly. In <code>legacy</code> mode gets the Viewer-tier capabilities only.</dd>
    </dl>

    <h3>Capabilities</h3>
    <p>
      A capability is one enforceable action, usually named
      <code>domain.action</code> (for example <code>workflow.promote</code>,
      <code>device.read</code>, <code>integration.manage</code>). A handful carry a
      third segment because the action itself is subdivided:
      <code>device.exec.read</code>, <code>device.exec.write</code>,
      <code>ai.permissions.read</code> and <code>job.stats.read</code>. Match the
      whole key, not the first two segments. Capabilities are defined in code — you
      cannot create new ones from the UI. The picker on <code>/permissions</code>
      groups them by domain; hover a pill to read its description.
    </p>
    <p>
      Each capability also has a <em>legacy tier</em> (Viewer, Operator or Admin).
      In <code>legacy</code> mode the tier alone decides: Viewer tier = any signed-in
      user, Operator tier = operator or admin, Admin tier = admin only.
    </p>

    <h3>Permission grants</h3>
    <p>
      A grant has a <strong>name</strong>, an optional <strong>description</strong>,
      an <strong>enabled</strong> flag, a list of <strong>capabilities</strong>, a list
      of <strong>subjects</strong> (users), and a <strong>conditions</strong> object.
      A grant does nothing until at least one user holds it, and a disabled grant
      gives its users nothing (they stay assigned).
    </p>
    <ul>
      <li>Grants only <strong>allow</strong>. There is no deny grant; to take something away, remove the user from the grant that gives it (or change their base role).</li>
      <li>A user's capabilities are the union of every enabled grant they hold.</li>
      <li>Names starting with <code>builtin.</code> are reserved.</li>
    </ul>

    <h3>Built-in grants</h3>
    <p>
      Two grants are seeded automatically and shown with a lock icon and a
      <em>built-in</em> badge:
    </p>
    <dl>
      <dt>builtin.viewer</dt>
      <dd>All Viewer-tier capabilities. Every active user whose base role is <code>viewer</code> is a subject.</dd>
      <dt>builtin.operator</dt>
      <dd>All Viewer- and Operator-tier capabilities. Every active user whose base role is <code>operator</code> is a subject.</dd>
    </dl>
    <p>
      Admins are not added to either grant (they bypass checks). Membership is
      re-synced at every backend start and whenever a user is created or their role
      changes. Deleting a user in <a href="/docs/admin/users">Users</a> marks the
      account inactive and leaves its id in the built-in grant's subject list, so
      the subject counts on those two cards include deactivated accounts; this
      grants nothing, because a deactivated account cannot sign in. The capability lists are refreshed from code at
      startup, and both
      grants are forced back to enabled. You cannot edit, disable, delete or change
      the subjects of a built-in grant: the API answers
      <code>built-in grants are managed automatically and cannot be modified</code>
      (code <code>builtin_readonly</code>). Because of these two grants, switching to
      <code>granular</code> mode reproduces the <code>legacy</code> behavior for most
      capabilities until you add your own grants.
    </p>

    <h3>Conditions</h3>
    <p>
      Conditions limit where a grant's capabilities apply. The stored shape is a JSON
      object; every key is optional:
    </p>
    <pre><code>{`{
  "environment": ["draft", "qa", "production"],
  "device_role":  ["<device-role>"],
  "device_pool":  ["<device-pool-name>"],
  "device_ids":   ["<device-uuid>"],
  "resource":     { "type": "workflow", "id": "<workflow-uuid>" },
  "mcp_server":   ["<mcp-server-uuid>"],
  "mcp_tool":     ["<tool-name>"]
}`}</code></pre>
    <ul>
      <li>An empty object <code>{'{}'}</code> means <strong>any context</strong>.</li>
      <li>Keys are combined with AND: every key you declare must be satisfied.</li>
      <li>Inside a list, any one value is enough (OR).</li>
      <li>If the check does not supply a value for a declared key, the grant <strong>does not match</strong>. A scoped grant never leaks into an unscoped check.</li>
      <li>Environment, device role and pool names are compared case-insensitively; MCP tool names are case-sensitive.</li>
    </ul>
    <p>
      Not every capability uses every condition. The API reports which dimensions each
      capability consults (<code>environment</code>, <code>device</code>,
      <code>resource</code>, <code>mcp_server</code>/<code>mcp_tool</code>), and the form
      shows the union for your selection, for example
      <em>Selected capabilities are scoped by: environment, resource.</em> If none apply
      it says <em>The selected capabilities take no conditions — they apply
      globally.</em>
    </p>
    <Callout tone="warning" title="Where conditions are enforced">
      Conditions are checked against the concrete context only at these points, and
      only in <code>granular</code> mode:
      <ul>
        <li>
          <strong>Running a workflow</strong> (<code>workflow.run</code>) — context is
          the workflow's environment and id and, for each target device the run will
          use, that device's id, role and pool names. Every device must be allowed.
          When the graph sends anything to a device, <code>device.exec.read</code> or
          <code>device.exec.write</code> is required for every device as well (see
          below).
        </li>
        <li><strong>Promoting a workflow</strong> (<code>workflow.promote</code>) — context is the <em>target</em> environment and the workflow id.</li>
        <li><strong>Calling an MCP tool</strong> (<code>mcp.execute</code>) from chat or when validating a workflow's MCP node — context is the server id and tool name.</li>
      </ul>
      Everywhere else the check is coarse: holding the capability in <em>any</em> grant
      is enough, whatever its conditions — with one important twist, described next.
    </Callout>
    <Callout tone="warning" title="Two save-time gates that a conditioned grant can never satisfy">
      <p>
        Saving a workflow runs two extra capability checks on the graph itself,
        because both hand a step something privileged to read:
      </p>
      <ul>
        <li>
          A node whose config contains a <code>${'{'}secret:…{'}'}</code> reference
          requires <code>secret.read</code> — the step receives the plaintext and
          can print it.
        </li>
        <li>
          A <code>git</code> step whose <code>operation</code> writes requires
          <code>git.manage</code>.
        </li>
      </ul>
      <p>
        Both fire only on what a save <em>adds</em>, and both name the node when
        they refuse. They are the exception to "coarse": they are checked with
        <strong>no context at all</strong>, and a grant matches an empty context
        only when it declares no conditions. So a grant that carries
        <code>secret.read</code> or <code>git.manage</code> together with
        <em>any</em> condition — even one as harmless-looking as
        <code>environment: ["draft"]</code> — will never satisfy these two gates.
        If you want a non-admin to save such steps, put those two capabilities in
        an unconditional grant of their own, and keep the conditioned
        capabilities in a separate one.
      </p>
    </Callout>

    <h3>device.exec.read and device.exec.write</h3>
    <p>
      Checked when a user starts a run in <code>granular</code> mode, per target
      device (scheduled, webhook and git-triggered runs are not a user's action and
      are not checked). The requirement comes from the graph:
    </p>
    <ul>
      <li><code>ssh</code> steps whose commands are all literal read commands (<code>show</code>, <code>display</code>, <code>get</code>, <code>ping</code>, <code>traceroute</code>, <code>tracert</code>, <code>info</code>) with no output redirect → <code>device.exec.read</code>.</li>
      <li>Any other <code>ssh</code> step — a configuration command, a template, a missing command, or a run input that carries <code>command</code>/<code>commands</code> → <code>device.exec.write</code>.</li>
      <li><code>ansible_playbook</code>, <code>netconf</code> and network-enabled Python → <code>device.exec.write</code>; <code>snmp_v3</code> → <code>device.exec.read</code>. (<code>netconf</code> and <code>snmp_v3</code> are recognized step types without an implementation: such a step always fails at run time, but the permission check still applies.)</li>
      <li><code>ping</code> and steps that never reach a device → nothing beyond <code>workflow.run</code>.</li>
    </ul>
    <p>
      A denial answers <code>403 permission_denied</code> naming the device and the
      missing capability, before any run is created.
    </p>
    <p>
      <strong>A run with no devices is still checked.</strong> When the request
      names no targets, or none of them resolves in the run's environment, the
      requirement is worked out from the graph exactly as above and then checked
      once against a context that carries the environment and the workflow id but
      no device dimensions. So a graph with an <code>ssh</code> step demands
      <code>device.exec.write</code> even on a run that would touch nothing — and
      a device-conditioned grant cannot answer a check with no device in it, so
      it does not cover that run. This is deliberate: the alternative would be to
      let an unscoped call through the gate that scoping exists to close.
    </p>
    <Callout tone="info" title="Which pool names the check sees">
      A <code>device_pool</code> condition is matched against <em>every active
      pool that lists the device</em> — not just the pools the run was launched
      with. Membership is the fact being tested, so a device that also sits in a
      pool you named in a grant satisfies that condition even when the run
      targeted it directly or through a different pool. Adding a device to a pool
      therefore widens every grant conditioned on that pool's name.
    </Callout>

    <h3>Per-resource roles (owner / editor / runner / viewer)</h3>
    <p>
      These are ranked <code>owner</code> &gt; <code>editor</code> &gt;
      <code>runner</code> &gt; <code>viewer</code>; holding a higher role satisfies a
      lower requirement. They are a separate mechanism from capability grants,
      and are checked only when <strong>Require per-resource Editor / Owner grants for
      workflow and integration writes</strong> is on in settings. When it is on:
    </p>
    <table>
      <thead><tr><th>Action</th><th>Required per-resource role</th></tr></thead>
      <tbody>
        <tr><td>Update a workflow or integration</td><td><code>editor</code> (or <code>owner</code>)</td></tr>
        <tr><td>Delete a workflow or integration</td><td><code>owner</code></td></tr>
        <tr><td>Import with <em>replace</em> onto an existing workflow</td><td><code>owner</code> on the target workflow — replace deactivates it, so it takes the same role as a delete</td></tr>
        <tr><td>Import with <em>update existing</em> on a structural duplicate</td><td><code>editor</code> (or <code>owner</code>) on the target workflow</td></tr>
      </tbody>
    </table>
    <p>
      The base role acts as a floor: admins always pass; operators pass up to
      <code>runner</code> without any grant; any signed-in user passes a
      <code>viewer</code> requirement. Anything above that needs an explicit grant on
      that resource. A per-resource role only raises access on that one resource — it
      never lowers what the base role already allows, and it cannot get past a
      capability check that fails first (a <code>viewer</code> with an
      <code>editor</code> grant is still stopped by <code>workflow.update</code> in
      <code>legacy</code> mode).
    </p>

    <h3>How the layers combine (precedence)</h3>
    <ol>
      <li><strong>Channel ceiling.</strong> A request that arrives through a messaging channel is capped by that channel's maximum role. This applies to admins too.</li>
      <li><strong>Admin bypass.</strong> Admins pass everything else.</li>
      <li><strong>Capability gate.</strong> <code>legacy</code>: the capability's tier versus the base role. <code>granular</code>: the user's enabled grants.</li>
      <li><strong>Conditioned check</strong> (<code>granular</code> only, at the three points listed above).</li>
      <li><strong>Per-resource check</strong> (only when the per-resource toggle is on, for update, delete and import-replace).</li>
      <li><strong>Policies.</strong> <a href="/docs/policies">Policies</a> can still block an allowed operation.</li>
    </ol>
    <p>
      There is no inheritance between resources: a grant on a workflow does not
      extend to its subflows, its integrations or its runs.
    </p>

    <h3>Effective permissions</h3>
    <p>
      A non-admin's effective capabilities in <code>granular</code> mode are the union
      of the capabilities of every enabled grant they hold (built-in ones included),
      then intersected with a channel ceiling if one applies. An admin's effective set
      is the whole catalog. FlowWeaver has no screen that shows a user's effective
      permissions; work them out by checking which grants list the user as a subject.
    </p>
  </section>

  <section>
    <h2>Role × capability matrix</h2>
    <p>
      What each base role gets in <code>legacy</code> mode, which is also what the
      built-in grants give in <code>granular</code> mode. ✓ = allowed, — = not
      allowed. The <em>Resource conditions</em> column lists what a grant for that
      capability can be scoped on.
    </p>
    <table>
      <thead>
        <tr><th>Capability</th><th>viewer</th><th>operator</th><th>admin</th><th>Resource conditions</th></tr>
      </thead>
      <tbody>
        <tr><td><code>workflow.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>workflow.create</code>, <code>workflow.update</code></td><td>—</td><td>✓</td><td>✓</td><td>environment, resource</td></tr>
        <tr><td><code>workflow.delete</code>, <code>workflow.clone</code></td><td>—</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>workflow.import</code></td><td>—</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>workflow.run</code></td><td>—</td><td>✓</td><td>✓</td><td>environment, device, resource</td></tr>
        <tr><td><code>workflow.promote</code>, <code>workflow.rollback</code></td><td>—</td><td>✓</td><td>✓</td><td>environment, resource</td></tr>
        <tr><td><code>run.read</code>, <code>run.cancel</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>run.delete</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>plan.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>plan.create</code>, <code>plan.submit</code></td><td>—</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>plan.approve</code>, <code>plan.build</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>trigger.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>trigger.manage</code></td><td>—</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>device.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>device</td></tr>
        <tr><td><code>device.manage</code></td><td>—</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>device.exec.read</code>, <code>device.exec.write</code></td><td>—</td><td>✓</td><td>✓</td><td>environment, device</td></tr>
        <tr><td><code>devicepool.*</code>, <code>inventory.*</code>, <code>credential.*</code> (<code>.read</code>)</td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>devicepool.*</code>, <code>inventory.*</code>, <code>credential.*</code> (<code>.manage</code>)</td><td>—</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>secret.read</code>, <code>secret.manage</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>integration.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>integration.manage</code></td><td>—</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>integration.test</code>, <code>integration.execute</code></td><td>—</td><td>✓</td><td>✓</td><td>environment, resource</td></tr>
        <tr><td><code>integrationaction.read</code> / <code>.manage</code></td><td>✓ / —</td><td>✓ / ✓</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>mcpserver.read</code> / <code>.manage</code></td><td>✓ / —</td><td>✓ / —</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>mcp.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>mcp.execute</code></td><td>—</td><td>✓</td><td>✓</td><td>MCP server, MCP tool</td></tr>
        <tr><td><code>snippet.*</code>, <code>skill.*</code>, <code>vendorcommand.*</code> (<code>.read</code> / <code>.manage</code>)</td><td>✓ / —</td><td>✓ / ✓</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>policy.read</code> / <code>policy.manage</code></td><td>✓ / —</td><td>✓ / —</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>access.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>access.manage</code></td><td>—</td><td>—</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>ai.chat</code>, <code>conversations.read</code>, <code>aicatalog.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>ai.permissions.read</code>, <code>aicatalog.reload</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>aiagent.read</code> / <code>.manage</code></td><td>✓ / —</td><td>✓ / ✓</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>aiprovider.read</code> / <code>.manage</code></td><td>✓ / —</td><td>✓ / —</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>promptskill.*</code>, <code>apispec.*</code>, <code>pythonmodule.*</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>git.read</code>, <code>gitwebhook.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>git.manage</code>, <code>gitwebhook.manage</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>messaging.link</code> / <code>messaging.manage</code></td><td>✓ / —</td><td>✓ / —</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>email.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>email.send</code></td><td>—</td><td>✓</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>email.manage</code></td><td>—</td><td>—</td><td>✓</td><td>resource</td></tr>
        <tr><td><code>report.read</code> / <code>report.manage</code></td><td>✓ / —</td><td>✓ / —</td><td>✓ / ✓</td><td>none</td></tr>
        <tr><td><code>qalab.read</code>, <code>job.stats.read</code></td><td>✓</td><td>✓</td><td>✓</td><td>none</td></tr>
        <tr><td><code>job.read</code>, <code>audit.read</code>, <code>trace.read</code>, <code>metrics.read</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
        <tr><td><code>user.read</code>, <code>user.manage</code>, <code>settings.read</code>, <code>settings.manage</code></td><td>—</td><td>—</td><td>✓</td><td>none</td></tr>
      </tbody>
    </table>
    <p>
      <strong>Workflow plans</strong> (<code>plan.*</code>) are a review path for
      changes headed to qa or production: a plan is created, submitted for
      approval, approved or rejected, and then built into a workflow. Plans have
      no screen: the AI assistant creates, submits and builds them with its
      tools, and the <code>/api/workflowplan</code> API offers the same steps
      (approving and rejecting are available only there).
    </p>
    <Callout tone="info" title="Some admin screens ignore grants">
      These APIs still check the <code>admin</code> role directly, in both modes:
      users, application settings, permission grants, audit log, traces, admin metrics,
      secrets, messaging channels, AI prompt skills, AI API specs and the Python package
      allowlist. Adding <code>user.manage</code>, <code>audit.read</code>,
      <code>secret.manage</code> and similar capabilities to a non-admin's grant does
      not open those screens. The sidebar also decides what to show by base role, not by
      grants.
    </Callout>

    <h3>Resource × base role (per-resource toggle on)</h3>
    <table>
      <thead>
        <tr><th>Per-resource role held</th><th>viewer: update / delete</th><th>operator: update / delete</th><th>admin: update / delete</th></tr>
      </thead>
      <tbody>
        <tr><td>none</td><td>— / —</td><td>— / —</td><td>✓ / ✓</td></tr>
        <tr><td><code>viewer</code> or <code>runner</code></td><td>— / —</td><td>— / —</td><td>✓ / ✓</td></tr>
        <tr><td><code>editor</code></td><td>— (blocked by capability) / —</td><td>✓ / —</td><td>✓ / ✓</td></tr>
        <tr><td><code>owner</code></td><td>— (blocked by capability) / —</td><td>✓ / ✓</td><td>✓ / ✓</td></tr>
      </tbody>
    </table>
    <p>
      The viewer column assumes <code>legacy</code> mode or a viewer without extra grants.
      In <code>granular</code> mode, a viewer who also holds <code>workflow.update</code>
      (or <code>integration.manage</code>) through a custom grant behaves like the
      operator column. With the toggle off, only the base role and capabilities decide.
    </p>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Permissions list (<code>/permissions</code>)</h3>
    <p>
      Header: <strong>Permissions</strong>, with <em>Refresh</em> and <em>New
      permission</em>. Each grant is a card:
    </p>
    <ul>
      <li><strong>Built-in grants</strong>: lock icon, name, <em>built-in</em> and <em>enabled</em> badges, the capability list and the subjects. No controls.</li>
      <li><strong>Your grants</strong>: name, <em>enabled</em>/<em>disabled</em> badge, a capability count (for example <em>3 caps</em>), a subject count when there is at least one, and the description. On the right: a power button (<em>Enable</em>/<em>Disable</em>) and a red <em>Delete</em> button. Click the name to expand the edit form.</li>
    </ul>
    <p>A counter at the bottom reads, for example, <em>5 permissions</em>.</p>

    <h3>New permission form</h3>
    <dl>
      <dt>Name</dt><dd>Required by the server. Placeholder <code>prod-ssh-operators</code>. Cannot start with <code>builtin.</code>.</dd>
      <dt>Enabled immediately</dt><dd>Checkbox, on by default.</dd>
      <dt>Description (optional)</dt><dd>Free text.</dd>
      <dt>Capabilities</dt><dd>Pill picker grouped by domain. At least one is required.</dd>
      <dt>Conditions</dt>
      <dd>
        Two tabs share the same value:
        <ul>
          <li><strong>Visual</strong> — <em>Environment</em> pills (<code>draft</code>, <code>qa</code>, <code>production</code>); <em>Device roles</em>, <em>Device pools (by name)</em> and <em>Device IDs</em> chip pickers filled from your existing devices and pools; <em>Resource (optional)</em> with a type box (<em>type — workflow, integration…</em>) and an id box (<em>resource UUID</em>); and a <em>Preview</em> of the resulting JSON (or <em>Empty — this grant applies in any context.</em>).</li>
          <li><strong>JSON</strong> — a <em>Conditions (JSON)</em> text area. Use this tab for <code>mcp_server</code> and <code>mcp_tool</code>, which the visual builder does not offer.</li>
        </ul>
        <Callout tone="warning" title="Editing in the Visual tab drops mcp_server and mcp_tool">
          The Visual tab handles <code>environment</code>, <code>device_role</code>,
          <code>device_pool</code>, <code>device_ids</code> and the resource type
          and id, and rewrites the whole JSON whenever you change one of its
          controls, removing <code>mcp_server</code> and <code>mcp_tool</code>.
          Just viewing the tab changes nothing. Add an MCP condition in the JSON
          tab last, save, and check the JSON after any later edit.
        </Callout>
      </dd>
    </dl>
    <p>
      Subjects are not chosen here: create the grant first, then expand it to add
      users. Buttons: <em>Cancel</em> and <em>Create</em>.
    </p>

    <h3>Edit form (expanded grant)</h3>
    <p>
      The same fields (<em>Name</em>, <em>Enabled</em>, <em>Description</em>,
      <em>Capabilities</em>, <em>Conditions</em>) plus <strong>Subjects</strong>: the
      assigned users as removable chips, an <em>Add user</em> dropdown (users who
      don't hold the grant yet) and an <em>Add</em> button. Adding or removing a
      subject is saved <strong>immediately</strong>; the other fields are saved only when
      you click <em>Save changes</em>.
    </p>

    <h3>Workflow / integration permissions</h3>
    <p>
      Title <strong>Workflow permissions</strong> or <strong>Integration
      permissions</strong>, then a panel <em>Per-workflow grants</em> /
      <em>Per-integration grants</em>. Admins see a form with <em>User</em>,
      <em>Role</em> (<code>owner</code>, <code>editor</code>, <code>runner</code>,
      <code>viewer</code>; default <code>viewer</code>) and <em>Grant</em> (disabled
      until a user is chosen). The table shows <em>User</em>, <em>Role</em> (colored
      badge), <em>Granted</em> (date and time), <em>By</em> and a <em>Revoke</em> button
      for admins.
    </p>
  </section>

  <section>
    <h2>Main procedure</h2>

    <h3>Create and assign a capability grant</h3>
    <ol>
      <li>Go to <strong>Govern → Permissions</strong> and click <strong>New permission</strong>.</li>
      <li>Enter a <strong>Name</strong> that describes the job (for example <code>qa-runner</code>) and, optionally, a description.</li>
      <li>Select the capabilities in the picker.</li>
      <li>Optionally add conditions in the <strong>Visual</strong> or <strong>JSON</strong> tab. Leave them empty for an unconditional grant.</li>
      <li>Click <strong>Create</strong>. A toast confirms <em>Permission "qa-runner" created</em>.</li>
      <li>Click the grant's name to expand it, choose a user in <strong>Add user</strong> and click <strong>Add</strong>. Repeat for each user.</li>
      <li>If the RBAC mode is <code>legacy</code>, the grant has no effect yet. Switch to <code>granular</code> in <a href="/docs/admin/settings">Application settings</a> when your grants are ready.</li>
    </ol>

    <h3>Grant a per-resource role</h3>
    <ol>
      <li>Open the workflow, click <strong>More → Permissions</strong> (or, for an integration, expand it on <code>/integrations</code> and click <strong>Permissions</strong>).</li>
      <li>Pick the <strong>User</strong> and the <strong>Role</strong>, then click <strong>Grant</strong>. A toast shows <em>Permission granted</em>.</li>
      <li>To remove it, click <strong>Revoke</strong> and confirm <em>Revoke access?</em></li>
      <li>Make sure <strong>Require per-resource Editor / Owner grants…</strong> is on in settings, or the grant is not checked.</li>
    </ol>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Goal:</strong> let <code>&lt;qa-user&gt;</code> run workflows in
      <code>qa</code> only, without full operator rights.
    </p>
    <p><strong>Prerequisites:</strong> <code>&lt;qa-user&gt;</code> exists with base role
      <code>viewer</code>. (If they were an operator, <code>builtin.operator</code> would
      already give them an unconditional <code>workflow.run</code>, and grants cannot take
      it away.)</p>
    <ol>
      <li>Create a grant named <code>qa-runner</code> with capabilities <code>workflow.read</code> and <code>workflow.run</code>.</li>
      <li>In <strong>Conditions → Visual</strong>, select the <code>qa</code> environment pill. The preview shows:
        <pre><code>{`{
  "environment": [
    "qa"
  ]
}`}</code></pre>
      </li>
      <li>Create it, expand it and add <code>&lt;qa-user&gt;</code> as a subject.</li>
      <li>In <code>/admin/settings</code>, set <strong>Mode</strong> to <code>granular</code> and save.</li>
    </ol>
    <p><strong>Expected result:</strong></p>
    <table>
      <thead><tr><th>Action by &lt;qa-user&gt;</th><th>Result</th></tr></thead>
      <tbody>
        <tr><td>Run a workflow whose environment is <code>qa</code> and whose steps never reach a device</td><td>Allowed — the run is queued.</td></tr>
        <tr><td>Run a <code>qa</code> workflow that does reach a device (an <code>ssh</code>, <code>ansible_playbook</code> or network-enabled Python step)</td><td>Denied. <code>workflow.read</code> and <code>workflow.run</code> are not enough: the run also needs <code>device.exec.read</code> or <code>device.exec.write</code>, and <code>qa-runner</code> has neither. Add the one the graph calls for to the grant.</td></tr>
        <tr><td>Run a workflow in <code>production</code></td><td>Denied: <code>you do not have permission to run this workflow in production</code> (403, code <code>permission_denied</code>).</td></tr>
        <tr><td>Edit a workflow</td><td>Denied — <code>workflow.update</code> is in neither <code>builtin.viewer</code> nor <code>qa-runner</code>.</td></tr>
      </tbody>
    </table>
    <p>
      <strong>Common error:</strong> the production run is still allowed. Cause: the mode
      is still <code>legacy</code> (conditions are ignored), or the user also holds
      <code>workflow.run</code> through another grant without conditions (for example
      <code>builtin.operator</code>). Fix: confirm the mode in settings, then check every
      grant that lists the user.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li>Listing, creating, editing, deleting grants and changing their subjects all require the <code>admin</code> role. Holding <code>access.manage</code> does not open <code>/permissions</code>.</li>
      <li>Listing per-resource roles requires <code>access.read</code> (every base role in <code>legacy</code> mode). Granting and revoking require <code>access.manage</code>; the UI shows the form only to admins.</li>
      <li>Every create, update and delete of a grant is recorded in the <a href="/docs/admin/audit">audit log</a> with the full capability list and conditions. Adding and removing a subject are recorded separately (<code>permission_grant.subject_added</code>, <code>permission_grant.subject_removed</code>). Per-resource grants and revokes are audited too.</li>
      <li>The same rules apply over messaging channels: a channel can only lower a user's access, never raise it (see <a href="/docs/ai/channels">Messaging channels</a>).</li>
      <li>In <code>granular</code> mode, the AI assistant's tool calls are checked against the capability each tool maps to; a tool with no mapped capability is denied for everyone, admins included.</li>
    </ul>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li><code>/permissions</code> shows a spinner while loading. With no grants: <em>No permission grants yet</em> and a <em>Create first permission</em> button. In practice the two built-in grants are always listed.</li>
      <li>If capabilities can't be loaded, the picker shows <em>No capabilities available.</em>; the JSON tab still works.</li>
      <li>Condition pickers with nothing to offer show <em>No device roles found</em>, <em>No device pools found</em> or <em>No devices found</em>.</li>
      <li>A grant with no users shows <em>No subjects assigned</em> (built-in) or <em>No subjects assigned yet.</em> (editable).</li>
      <li>The per-resource panel shows <em>Loading permissions…</em>, then <em>No per-resource grants</em> with the note that global RBAC (admin/operator/viewer) still applies. Non-admins see <em>Granting and revoking permissions is restricted to administrators.</em></li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>Message</th><th>Cause</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr><td><code>Select at least one capability.</code></td><td>Create clicked with no capability.</td><td>Pick at least one pill.</td></tr>
        <tr><td><code>Conditions JSON invalid: …</code></td><td>The JSON tab holds text that is not a JSON object.</td><td>Fix the JSON or go back to <em>Visual</em>. An empty object <code>{'{}'}</code> is valid.</td></tr>
        <tr><td><code>name is required</code> / <code>name cannot be blank</code></td><td>Empty name.</td><td>Enter a name.</td></tr>
        <tr><td><code>'builtin.' is a reserved name prefix</code></td><td>Name starts with <code>builtin.</code>.</td><td>Choose another name.</td></tr>
        <tr><td><code>unknown capabilities: …</code></td><td>A JSON/API payload names a capability that doesn't exist.</td><td>Use keys from the picker.</td></tr>
        <tr><td><code>conditions must be a JSON object</code></td><td>Conditions sent as an array or a scalar.</td><td>Wrap them in <code>{'{ }'}</code>.</td></tr>
        <tr><td><code>built-in grants are managed automatically and cannot be modified</code></td><td>API call against <code>builtin.viewer</code> or <code>builtin.operator</code>.</td><td>Change the user's base role instead, or create your own grant.</td></tr>
        <tr><td><code>missing_editor_grant</code> / <code>missing_owner_grant</code></td><td>Per-resource toggle is on and the user lacks the role on that workflow or integration.</td><td>An admin grants <code>editor</code> or <code>owner</code> on the resource, or turns the toggle off.</td></tr>
        <tr><td><code>you do not have permission to run this workflow in &lt;env&gt;</code> / <code>…to promote to &lt;env&gt;</code></td><td><code>granular</code> mode; no grant matches that environment and workflow.</td><td>Add the environment (or remove the condition) in a grant the user holds.</td></tr>
        <tr><td><em>You don’t have permission to perform this action.</em></td><td>The capability gate failed (no body from the server).</td><td>Give the user the capability through a grant, or change their base role.</td></tr>
      </tbody>
    </table>

    <h3>Lockout recovery</h3>
    <p>
      Admins cannot lock themselves out: the admin role bypasses every capability and
      per-resource check, and the settings, users and permission-grant APIs check the
      admin role directly. If non-admins lose access after a change:
    </p>
    <ol>
      <li>Sign in as an admin and open <code>/admin/settings</code>.</li>
      <li>Set <strong>Mode</strong> back to <code>legacy</code> and/or clear <strong>Require per-resource Editor / Owner grants…</strong>, then <strong>Save changes</strong>.</li>
      <li>Allow up to a minute for the change to take effect everywhere.</li>
      <li>Fix the grants, then switch back.</li>
    </ol>
    <p>
      A disabled or deleted grant is not restored automatically. Re-enable a disabled
      grant with its power button. A deleted grant is gone from the list; recreate it
      using the before-snapshot in the <a href="/docs/admin/audit">audit log</a>.
    </p>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>Conditions are enforced only for running and promoting workflows and calling MCP tools. Other capabilities ignore them — except the <code>secret.read</code> and <code>git.manage</code> save-time gates, which are checked with no context and so are <em>defeated</em> by any condition rather than ignoring it.</li>
      <li><code>device.exec.*</code> is decided from the saved graph when the run starts, not per command at execution time, and unattended runs (schedules, webhooks, git) are not checked.</li>
      <li>The per-resource <code>runner</code> and <code>viewer</code> roles have no effect: running and reading are not checked per resource.</li>
      <li>An <code>owner</code> cannot grant roles on their own resource. In <code>legacy</code> mode only admins can, because <code>access.manage</code> is an Admin-tier capability; in <code>granular</code> mode a grant carrying <code>access.manage</code> lets a non-admin grant per-resource roles through the API, even though the page hides the form from them.</li>
      <li>Subjects are individual users; groups are not supported.</li>
      <li>No screen shows a user's effective permissions.</li>
      <li>The list page loads at most 100 grants.</li>
      <li>The visual condition builder offers at most 500 devices and 200 pools, and has no fields for <code>mcp_server</code> or <code>mcp_tool</code> (use the JSON tab — and note that editing anything in the Visual tab afterwards erases them).</li>
      <li>The resource condition accepts any text; a type other than <code>workflow</code> or a value that is not a UUID never matches.</li>
      <li>Deleting a grant has no undo.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/admin/settings">Application settings</a> — RBAC mode and the per-resource toggle.</li>
      <li><a href="/docs/admin/users">Users</a> — base roles; changing a role re-syncs the built-in grants.</li>
      <li><a href="/docs/policies">Policies</a> — deny rules evaluated after permissions.</li>
      <li><a href="/docs/admin/mcp-servers">MCP servers</a> — scoping <code>mcp.execute</code> by server and tool.</li>
      <li><a href="/docs/ai/channels">Messaging channels</a> — the per-channel role ceiling.</li>
      <li><a href="/docs/admin/audit">Audit log</a> — who changed which grant.</li>
    </ul>
  </section>
</DocLayout>
