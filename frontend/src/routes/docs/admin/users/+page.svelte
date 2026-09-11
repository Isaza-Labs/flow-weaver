<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Users"
  lead="Create accounts, assign roles, disable access, rotate passwords. The admin-only surface for managing who can log in and what they can do."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/users"><code>/admin/users</code></a>.
    Sidebar → Admin dashboard → Users link.
  </Callout>
  <Callout tone="admin" title="Admin only">
    Requires the <code>admin</code> role.
  </Callout>

  <section>
    <h2>Roles</h2>
    <p>
      FlowWeaver has three built-in roles. Every account gets exactly one.
    </p>
    <dl>
      <dt>admin</dt>
      <dd>
        Full control. Sees every sidebar section, plus the Govern
        group and the Admin dashboard. Can create/edit users, policies,
        bulk-delete conversations and runs, etc.
      </dd>
      <dt>operator</dt>
      <dd>
        Day-to-day builder. Full CRUD on Build and Operate resources but no
        access to Govern or Administration.
      </dd>
      <dt>viewer</dt>
      <dd>
        Read-only. Can browse runs, schedules, the QA lab, and chat with the
        AI. Tool calls that need write access return 403 at the tool
        boundary.
      </dd>
    </dl>
    <Callout tone="warning" title="One role per user">
      A user has exactly one role — no composite roles or per-resource ACLs
      today. If you need finer-grained access, create multiple accounts or
      use the <a href="/docs/policies">Policies</a> surface to add blocks.
    </Callout>
  </section>

  <section>
    <h2>List page</h2>
    <p>
      Route: <code>/admin/users</code>. Header with a back arrow to
      <code>/admin</code>, a <em>New user</em> button, and a refresh action.
    </p>

    <h3>Columns</h3>
    <table>
      <thead><tr><th>Column</th><th>Meaning</th></tr></thead>
      <tbody>
        <tr><td>Username</td><td>Login name, must be unique.</td></tr>
        <tr><td>Email</td><td>Contact address. Also unique.</td></tr>
        <tr><td>Role</td><td>Tone-coded badge (<em>admin</em> primary, <em>operator</em> success, <em>viewer</em> neutral).</td></tr>
        <tr><td>Status</td><td><em>Active</em> or <em>Disabled</em>. Disabled accounts can't sign in.</td></tr>
        <tr><td>Created</td><td>Account creation date.</td></tr>
        <tr><td>Actions</td><td>Edit, delete.</td></tr>
      </tbody>
    </table>
  </section>

  <section>
    <h2>Create user</h2>
    <p>
      Opens from <em>New user</em>. Fields:
    </p>
    <dl>
      <dt>Username</dt><dd>Required.</dd>
      <dt>Email</dt><dd>Required.</dd>
      <dt>Password</dt>
      <dd>Required. Minimum 8 characters (enforced client-side and server-side).</dd>
      <dt>Role</dt>
      <dd>
        Select among <code>admin</code>, <code>operator</code>,
        <code>viewer</code>. Default is <code>viewer</code> — minimum
        privilege out of the box.
      </dd>
    </dl>
    <p>
      The list inserts the new user alphabetically on success and toasts the
      username.
    </p>
  </section>

  <section>
    <h2>Edit user</h2>
    <p>
      Opens from the pencil icon. Fields you can change:
    </p>
    <dl>
      <dt>Email</dt><dd>Any valid address.</dd>
      <dt>Role</dt><dd>Change the role anytime.</dd>
      <dt>Active</dt><dd>Flip to disable the account without deleting it. Disabled accounts stay in the list for audit purposes.</dd>
    </dl>
    <p>
      Passwords are reset separately via the <em>Change password</em> action
      (key icon). The admin types a new password; the user will use it next
      time they sign in.
    </p>
  </section>

  <section>
    <h2>Delete</h2>
    <p>
      Confirmation dialog. Deleting a user removes the row.
      Historical audit and trace events keep the <code>user_id</code>
      reference so the past stays intact.
    </p>
    <Callout tone="warning">
      Prefer disabling over deleting. Disable when someone leaves; only
      delete when you've also confirmed there's no need to audit their past
      actions under a searchable name.
    </Callout>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/getting-started">Getting started</a> — the three roles and what they see.</li>
      <li><a href="/docs/admin/audit">Audit log</a> — review what each user did.</li>
      <li><a href="/docs/account">Account & session</a> — how users manage their own password.</li>
    </ul>
  </section>
</DocLayout>
