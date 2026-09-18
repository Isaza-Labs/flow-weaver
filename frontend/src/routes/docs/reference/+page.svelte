<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Screens and roles"
  lead="Every screen in FlowWeaver with its route, the minimum base role that can use it, and the chapter that explains it — followed by the role × action matrix and the conventions used in examples."
>
  <Callout tone="where" title="Where to find it">
    Manual → <strong>Reference</strong> → <strong>Screens and roles</strong>.
  </Callout>

  <section>
    <h2>Navigation matrix</h2>
    <p>
      <em>Minimum role</em> is the lowest base role that can open the screen and
      use its main purpose. <code>viewer</code> screens are read-only for viewers;
      writing needs <code>operator</code> unless the chapter says otherwise.
      <em>admin</em> screens are hidden from the sidebar for everyone else, and the
      server rejects their calls — unless the row says otherwise, because a few
      screens are admin-only in the sidebar while their read endpoints are open to
      viewers. With <a href="/docs/permissions">granular
      permissions</a> an admin can widen or narrow some of this per user.
    </p>
    <table>
      <thead>
        <tr><th>Screen</th><th>Route</th><th>Minimum role</th><th>Chapter</th></tr>
      </thead>
      <tbody>
        <tr><td>Sign in</td><td><code>/login</code></td><td>public</td><td><a href="/docs/getting-started">Getting started</a></td></tr>
        <tr><td>Sign out</td><td><code>/logout</code></td><td>public</td><td><a href="/docs/account">Account &amp; session</a></td></tr>
        <tr><td>Link a chat account</td><td><code>/link?token=…</code></td><td>public page, confirming needs any signed-in user</td><td><a href="/docs/ai/channels">Messaging channels</a></td></tr>
        <tr><td>Dashboard</td><td><code>/</code></td><td>viewer</td><td><a href="/docs/dashboard">Dashboard</a></td></tr>
        <tr><td>Workflows list</td><td><code>/workflows</code></td><td>viewer</td><td><a href="/docs/workflows">Workflows</a></td></tr>
        <tr><td>Workflow editor</td><td><code>/workflows/{'{id}'}</code></td><td>viewer (read-only)</td><td><a href="/docs/workflows">Workflows</a></td></tr>
        <tr><td>Import workflow</td><td><code>/workflows/import</code></td><td>operator</td><td><a href="/docs/workflows/import">Importing workflows</a></td></tr>
        <tr><td>Workflow schedules</td><td><code>/workflows/{'{id}'}/schedules</code> (and <code>/new</code>, <code>/{'{triggerId}'}/edit</code>)</td><td>viewer (read-only)</td><td><a href="/docs/schedules">Schedules</a></td></tr>
        <tr><td>Workflow triggers</td><td><code>/workflows/{'{id}'}/triggers</code></td><td>viewer (read-only)</td><td><a href="/docs/workflows/triggers">Triggers and webhooks</a></td></tr>
        <tr><td>Workflow permissions</td><td><code>/workflows/{'{id}'}/permissions</code></td><td>admin to change</td><td><a href="/docs/permissions">Permissions</a></td></tr>
        <tr><td>Subflows</td><td><code>/subflows</code></td><td>viewer</td><td><a href="/docs/subflows">Subflows</a></td></tr>
        <tr><td>Snippets</td><td><code>/snippets</code>, <code>/snippets/new</code>, <code>/snippets/{'{id}'}</code></td><td>viewer (read-only)</td><td><a href="/docs/snippets">Snippets</a></td></tr>
        <tr><td>Git repositories</td><td><code>/integrations/git</code>, <code>/integrations/git/{'{id}'}</code></td><td>viewer (read-only)</td><td><a href="/docs/git-repos">Git repositories</a></td></tr>
        <tr><td>Integrations</td><td><code>/integrations</code></td><td>viewer (read-only)</td><td><a href="/docs/integrations">Integrations</a></td></tr>
        <tr><td>Integration deep link</td><td><code>/integrations/{'{id}'}</code></td><td>viewer</td><td>Redirects to <code>/integrations?expand={'{id}'}</code> — <a href="/docs/integrations">Integrations</a></td></tr>
        <tr><td>Integration permissions</td><td><code>/integrations/{'{id}'}/permissions</code></td><td>admin to change</td><td><a href="/docs/permissions">Permissions</a></td></tr>
        <tr><td>Vendor commands</td><td><code>/vendor-commands</code></td><td>viewer to read, operator to manage (sidebar shows it to admins only)</td><td><a href="/docs/vendor-commands">Vendor commands</a></td></tr>
        <tr><td>MCP servers</td><td><code>/admin/mcp-servers</code></td><td>admin</td><td><a href="/docs/admin/mcp-servers">MCP servers</a></td></tr>
        <tr><td>Messaging channels</td><td><code>/admin/messaging-channels</code></td><td>admin</td><td><a href="/docs/ai/channels">Messaging channels</a></td></tr>
        <tr><td>Email</td><td><code>/email</code></td><td>viewer to read the channel list, operator to send a test, admin to configure (sidebar shows it to admins only)</td><td><a href="/docs/email">Email</a></td></tr>
        <tr><td>Runs</td><td><code>/runs</code>, <code>/runs/{'{id}'}</code>, <code>/runs/{'{id}'}/monitor</code></td><td>viewer</td><td><a href="/docs/runs">Runs</a></td></tr>
        <tr><td>Schedules (all)</td><td><code>/schedules</code>, <code>/schedules/calendar</code></td><td>viewer</td><td><a href="/docs/schedules">Schedules</a></td></tr>
        <tr><td>Devices</td><td><code>/devices</code></td><td>viewer (read-only)</td><td><a href="/docs/devices">Devices</a></td></tr>
        <tr><td>Device pools</td><td><code>/device-pools</code></td><td>viewer (read-only)</td><td><a href="/docs/device-pools">Device pools</a></td></tr>
        <tr><td>Credentials</td><td><code>/credentials</code></td><td>viewer (metadata only)</td><td><a href="/docs/credentials">Credentials</a></td></tr>
        <tr><td>QA lab</td><td><code>/qa</code></td><td>viewer</td><td><a href="/docs/qa-lab">QA lab</a></td></tr>
        <tr><td>Artifacts</td><td><code>/admin/artifacts</code>, <code>/admin/artifacts/{'{id}'}</code></td><td>admin</td><td><a href="/docs/admin/artifacts">Artifacts</a></td></tr>
        <tr><td>Policies</td><td><code>/policies</code>, <code>/policies/audit</code></td><td>viewer to read, admin to author (sidebar shows it to admins only)</td><td><a href="/docs/policies">Policies</a></td></tr>
        <tr><td>Permissions</td><td><code>/permissions</code></td><td>admin</td><td><a href="/docs/permissions">Permissions</a></td></tr>
        <tr><td>Python packages</td><td><code>/admin/python-packages</code></td><td>admin</td><td><a href="/docs/admin/python-packages">Python packages</a></td></tr>
        <tr><td>AI hub</td><td><code>/ai</code></td><td>viewer</td><td><a href="/docs/ai">AI overview</a></td></tr>
        <tr><td>AI chat</td><td><code>/ai/chat</code></td><td>viewer</td><td><a href="/docs/ai/chat">Chat</a></td></tr>
        <tr><td>AI providers</td><td><code>/ai/providers</code></td><td>viewer (read-only)</td><td><a href="/docs/ai/providers">Providers</a></td></tr>
        <tr><td>AI agents</td><td><code>/ai/agents</code></td><td>viewer (read-only)</td><td><a href="/docs/ai/agents">Agents</a></td></tr>
        <tr><td>Prompt skills</td><td><code>/ai/skills</code></td><td>admin</td><td><a href="/docs/ai/skills">Skills</a></td></tr>
        <tr><td>API specs</td><td><code>/ai/specs</code></td><td>admin</td><td><a href="/docs/ai/specs">API specs</a></td></tr>
        <tr><td>Admin dashboard</td><td><code>/admin</code></td><td>admin</td><td><a href="/docs/admin">Admin dashboard</a></td></tr>
        <tr><td>Users</td><td><code>/admin/users</code></td><td>admin</td><td><a href="/docs/admin/users">Users</a></td></tr>
        <tr><td>Audit log</td><td><code>/admin/audit</code></td><td>admin</td><td><a href="/docs/admin/audit">Audit log</a></td></tr>
        <tr><td>Traces</td><td><code>/admin/traces</code></td><td>admin</td><td><a href="/docs/admin/traces">Traces</a></td></tr>
        <tr><td>Secrets</td><td><code>/admin/secrets</code></td><td>admin</td><td><a href="/docs/admin/secrets">Secrets</a></td></tr>
        <tr><td>Service-level objectives</td><td><code>/admin/slo</code></td><td>admin</td><td><a href="/docs/admin/slo">Service-level objectives</a></td></tr>
        <tr><td>Application settings</td><td><code>/admin/settings</code></td><td>admin</td><td><a href="/docs/admin/settings">Application settings</a></td></tr>
        <tr><td>Reusable actions (legacy)</td><td><code>/admin/actions</code></td><td>admin</td><td><a href="/docs/admin/actions">Reusable actions (legacy)</a></td></tr>
        <tr><td>Change password</td><td><code>/settings/password</code></td><td>viewer</td><td><a href="/docs/account">Account &amp; session</a></td></tr>
        <tr><td>Theme studio</td><td><code>/themes</code></td><td>viewer (publishing: admin)</td><td><a href="/docs/themes">Themes</a></td></tr>
        <tr><td>User manual</td><td><code>/docs</code></td><td>viewer</td><td>This manual</td></tr>
      </tbody>
    </table>
    <Callout tone="info" title="Screens not in the sidebar">
      <code>/admin/secrets</code> is reached from the AI hub's <em>Agent
      composition</em> card, <code>/admin/slo</code> and
      <code>/admin/settings</code> from the admin dashboard, and
      <code>/admin/actions</code> only by typing its URL.
    </Callout>
  </section>

  <section>
    <h2>Role × action matrix</h2>
    <p>
      The everyday picture for the three base roles in the default
      (<code>legacy</code>) RBAC mode. The capability-level matrix, and how grants
      change it, is in <a href="/docs/permissions">Permissions</a>. Each chapter's
      <em>Role differences</em> section only lists exceptions to this table.
    </p>
    <table>
      <thead>
        <tr><th>Area</th><th>viewer</th><th>operator</th><th>admin</th></tr>
      </thead>
      <tbody>
        <tr><td>Workflows, subflows</td><td>view</td><td>create, edit, delete, clone, import, run</td><td>same</td></tr>
        <tr><td>Promotion and rollback</td><td>—</td><td>promote and roll back (production still needs a second approver)</td><td>same</td></tr>
        <tr><td>Schedules and webhook triggers</td><td>view</td><td>create, edit, enable, delete (signed webhooks only)</td><td>also allow unsigned webhooks</td></tr>
        <tr><td>Runs</td><td>view, monitor, cancel</td><td>same</td><td>also delete one or all runs</td></tr>
        <tr><td>Snippets</td><td>view</td><td>create, edit, delete, test</td><td>also network-enabled flag</td></tr>
        <tr><td>Integrations and actions</td><td>view</td><td>create, edit, delete, health check, test</td><td>same</td></tr>
        <tr><td>Git repositories</td><td>view</td><td>view; save <code>git</code> read steps</td><td>register, edit, delete, pull, push, switch branch, webhooks; save <code>git</code> write steps</td></tr>
        <tr><td>Devices, pools, credentials</td><td>view (credential metadata only)</td><td>create, edit, delete</td><td>same</td></tr>
        <tr><td>Vendor commands</td><td>view (by URL)</td><td>manage (by URL)</td><td>manage (in the sidebar)</td></tr>
        <tr><td>AI chat</td><td>chat; write tools fail with 403</td><td>chat with write tools</td><td>also see and clear all conversations</td></tr>
        <tr><td>AI providers</td><td>view</td><td>view</td><td>create, edit, delete</td></tr>
        <tr><td>AI agents</td><td>view</td><td>create, edit, delete</td><td>same</td></tr>
        <tr><td>Prompt skills, API specs</td><td>—</td><td>—</td><td>full (the API is admin-only)</td></tr>
        <tr><td>MCP servers</td><td>discover tools</td><td>discover and call tools</td><td>also register and manage servers</td></tr>
        <tr><td>Messaging channels</td><td>link own account</td><td>link own account</td><td>configure channels</td></tr>
        <tr><td>Email</td><td>—</td><td>send test</td><td>configure channels</td></tr>
        <tr><td>Policies</td><td>view the policies and the policy audit (by URL)</td><td>same</td><td>also create, edit and delete policies</td></tr>
        <tr><td>Permissions, Python packages, secrets, settings</td><td>—</td><td>—</td><td>full, including saving steps that reference a secret</td></tr>
        <tr><td>Users, audit, traces, artifacts, SLOs</td><td>—</td><td>—</td><td>full</td></tr>
        <tr><td>Own password, themes</td><td>yes</td><td>yes</td><td>also publish shared themes</td></tr>
      </tbody>
    </table>
    <Callout tone="warning" title="Where a chapter disagrees">
      Some chapters were written before capability-based permissions existed. When a
      chapter's <em>Role differences</em> and this table disagree, the
      <a href="/docs/permissions">Permissions</a> matrix, which is derived from the
      capability catalogue, is the reference.
    </Callout>
  </section>

  <section>
    <h2>Conventions used in examples</h2>
    <ul>
      <li><strong>Placeholders</strong> are written in angle brackets — <code>&lt;workflow-id&gt;</code>, <code>&lt;your-repo-url&gt;</code>. Replace the whole thing, brackets included.</li>
      <li><strong>No real secrets.</strong> Tokens and passwords in examples are fake; never paste a real one into a ticket or chat to reproduce an example.</li>
      <li><strong>Prerequisites and role</strong> are stated before each procedure.</li>
      <li><strong>Expected result</strong> follows each procedure, so you can tell success from a silent failure.</li>
      <li><strong>Errors</strong>: each chapter's <em>Errors and recovery</em> section gives the most frequent failure and the fix; <a href="/docs/troubleshooting">Troubleshooting</a> indexes them by symptom.</li>
      <li><strong>Formatting</strong>: <code>monospace</code> for routes, field keys, values and commands; <em>italics</em> for on-screen labels; <strong>bold</strong> for key terms; code blocks for JSON, YAML and shell. <code>{'{{ … }}'}</code> is a FlowWeaver template, not a placeholder.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/permissions">Permissions</a> — capabilities, grants and per-resource roles.</li>
      <li><a href="/docs/troubleshooting">Troubleshooting</a> — find the fix by symptom.</li>
      <li><a href="/docs/glossary">Glossary</a> — the terms used across the manual.</li>
    </ul>
  </section>
</DocLayout>
