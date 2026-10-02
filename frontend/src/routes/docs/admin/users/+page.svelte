<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Users"
  lead="Create accounts, assign roles and remove access. The admin-only surface for managing who can log in and what they can do."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/users"><code>/admin/users</code></a>.
    User menu → Admin dashboard → Tools → Users card.
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
        Day-to-day builder. Creates and edits workflows, subflows, snippets,
        integrations, devices, pools, credentials and schedules; no access to
        Govern or Administration. See
        <a href="/docs/reference">Screens and roles</a> for the exceptions.
      </dd>
      <dt>viewer</dt>
      <dd>
        Read-only. Can browse runs, schedules, the QA lab, and chat with the
        AI. Tool calls that need write access return 403 at the tool
        boundary.
      </dd>
    </dl>
    <Callout tone="warning" title="One base role per user">
      A user has exactly one base role. Finer-grained access is layered on top
      rather than expressed as extra roles: capability grants (optionally
      limited to an environment, device or resource) and per-workflow /
      per-integration access — see <a href="/docs/permissions">Permissions</a>.
      <a href="/docs/policies">Policies</a> add blocks that apply to everyone.
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
        <tr><td>Status</td><td><em>Active</em>. The list only shows active accounts: a disabled or deleted account leaves it (see below).</td></tr>
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
      <dd>
        Required. Must satisfy the password policy; by default at least 12
        characters with upper- and lower-case letters, a digit and a symbol.
        It must not contain the username or the part of the email before
        <code>@</code>, and common passwords are rejected.
      </dd>
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
      <dt>Active</dt><dd>Unchecking Active disables sign-in. The account then disappears from this list and can no longer be opened or edited here, exactly as after a delete.</dd>
    </dl>
    <p>
      The username can't be changed. There is no password-reset action on
      this screen: users change their own password at
      <a href="/settings/password"><code>/settings/password</code></a>.
    </p>
  </section>

  <section>
    <h2>Delete</h2>
    <p>
      Confirmation dialog. Deleting is a soft delete: the account is marked
      inactive, can't sign in and leaves this list; the record is kept, so
      audit and trace events still resolve. You can't delete your own
      account.
    </p>
    <Callout tone="warning">
      Disabling and deleting have the same result here: this screen can't
      restore the account, and its username stays taken (creating it again
      fails with <em>username already exists</em>). Open sessions keep
      working until their refresh tokens expire, as the confirmation dialog
      says.
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
