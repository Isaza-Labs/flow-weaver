<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Account & session"
  lead="The small settings area every user can reach, regardless of role. Covers the user menu, changing your password, and the session lifecycle."
>
  <Callout tone="where" title="Where to find it">
    User menu (your avatar in the sidebar footer) →
    <em>Change password</em> / <em>Sign out</em>.
    Password URL: <a href="/settings/password"><code>/settings/password</code></a>.
  </Callout>

  <section>
    <h2>The user menu</h2>
    <p>
      Click your circular avatar in the sidebar footer. A small popover opens
      upward from the avatar with:
    </p>
    <ul>
      <li>
        <strong>Identity header</strong> — your username and role
        (<code>admin</code>, <code>operator</code>, <code>viewer</code>), not
        clickable.
      </li>
      <li>
        <strong>Admin dashboard</strong> — shown only to admins. Links to
        <code>/admin</code>.
      </li>
      <li>
        <strong>Change password</strong> — links to
        <code>/settings/password</code>.
      </li>
      <li>
        <strong>Sign out</strong> — clears your session and sends you to
        <code>/login</code>.
      </li>
    </ul>
    <p>
      Click anywhere outside the popover to close it.
    </p>
  </section>

  <section>
    <h2>Change password</h2>
    <p>
      Route: <code>/settings/password</code>. A compact form inside a single
      card.
    </p>

    <h3>Fields</h3>
    <dl>
      <dt>Current password</dt>
      <dd>
        Required. The server checks it before accepting the new value.
      </dd>
      <dt>New password</dt>
      <dd>Required. Minimum 8 characters.</dd>
      <dt>Confirm new password</dt>
      <dd>
        Must match <em>New password</em> exactly. Mismatches are caught
        client-side before the request goes out.
      </dd>
    </dl>

    <h3>Outcomes</h3>
    <ul>
      <li>Success → a green toast <em>"Password changed successfully"</em>. All three fields reset.</li>
      <li>Validation error (empty field, mismatch, too short) → a red alert at the top of the form with the specific reason.</li>
      <li>Server rejection (wrong current password) → the same alert style with the server's message.</li>
    </ul>
    <Callout tone="warning">
      Changing your password does not invalidate other active sessions. If
      you suspect a credential leak, ask an admin to rotate your password
      from <a href="/admin/users"><code>/admin/users</code></a> — that
      revokes refresh tokens too.
    </Callout>
  </section>

  <section>
    <h2>Session lifecycle</h2>
    <p>
      Sessions use JWT access tokens plus a refresh token. Both are stored
      in browser <code>localStorage</code> under
      <code>flowweaver:auth</code>.
    </p>
    <dl>
      <dt>Access token TTL</dt>
      <dd>Short-lived (minutes). Attached as <code>Authorization: Bearer …</code> on every API call.</dd>
      <dt>Refresh token TTL</dt>
      <dd>Longer-lived. Used to mint new access tokens without re-entering credentials.</dd>
      <dt>Expiring-soon heuristic</dt>
      <dd>If the access token expires within the next 30 seconds, the client refreshes preemptively before the call to avoid a 401 round-trip.</dd>
      <dt>Multiple tabs</dt>
      <dd>
        Refresh tokens rotate, so all tabs coordinate: the refresh runs one at
        a time across tabs (Web Locks), and a rotation in one tab is picked up
        by the others via the <code>storage</code> event. This prevents a
        lagging tab from replaying a just-rotated token — which the backend's
        reuse detection would treat as theft and use to revoke every session.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Sign out</h2>
    <p>
      Opens <code>/logout</code>, which clears the tokens from local storage
      and redirects to <code>/login</code>. Any open tab that tries to hit
      an authenticated route will bounce to login too, preserving the URL as
      a <code>redirect</code> query parameter.
    </p>
  </section>

  <section>
    <h2>Theme and guide mascot</h2>
    <p>
      Two small preferences live in the sidebar footer (next to the version
      stamp) and are saved per-browser in local storage:
    </p>
    <dl>
      <dt>Theme toggle</dt><dd>Switches between light and dark. No account-level setting — purely per-device.</dd>
      <dt>Guide mascot button</dt><dd>A question-mark icon that re-enables the floating guide mascot if you previously dismissed it.</dd>
    </dl>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/getting-started">Getting started</a> — the full app-shell tour including the user menu.</li>
      <li><a href="/docs/admin/users">Users</a> — admin-side password management.</li>
    </ul>
  </section>
</DocLayout>
