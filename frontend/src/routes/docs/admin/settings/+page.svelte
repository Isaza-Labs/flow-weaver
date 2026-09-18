<script lang="ts">
  import DocLayout from '../../_components/DocLayout.svelte';
  import Callout from '../../_components/Callout.svelte';
</script>

<DocLayout
  title="Application settings"
  lead="Settings that apply to the whole deployment: which permission system is in charge, whether per-resource roles are checked, and how strictly the import wizard matches action names."
>
  <Callout tone="where" title="Where to find it">
    URL: <a href="/admin/settings"><code>/admin/settings</code></a>.
    User menu → <strong>Admin dashboard</strong> → <strong>Tools</strong> →
    <strong>Settings</strong> card. The page title is <strong>Settings</strong>, with the
    breadcrumb <em>Admin › Settings</em>.
  </Callout>
  <Callout tone="admin" title="Admin only">
    Requires the <code>admin</code> role. Non-admins who open the URL are sent back to
    the dashboard, and the settings API rejects them. The admin check uses the base
    role directly, so no setting on this page can lock an admin out of it.
  </Callout>

  <section>
    <h2>Purpose</h2>
    <p>
      One page, three cards, one <em>Save changes</em> button. Two cards control how
      requests are authorized (see <a href="/docs/permissions">Permissions</a>); the
      third tunes the workflow import wizard. Every change affects every user, so
      read the impact notes before you save.
    </p>
  </section>

  <section>
    <h2>Before you start</h2>
    <ul>
      <li>Sign in as an admin.</li>
      <li>Before you turn on the per-resource toggle, give operators <code>editor</code> or <code>owner</code> on the workflows and integrations they change.</li>
      <li>Before you switch to <code>granular</code>, check that the grants under <a href="/permissions">Govern → Permissions</a> cover what your users need.</li>
      <li>Tell your users. Both authorization changes can take write access away from people in the middle of their work.</li>
    </ul>
  </section>

  <section>
    <h2>Concepts and limits</h2>
    <dl>
      <dt>Defaults</dt>
      <dd>A new deployment has nothing stored. Until the first save it uses: toggle <strong>off</strong>, mode <code>legacy</code>, minimum score <code>0.80</code>, margin <code>0.10</code>. The first save stores all four values.</dd>
      <dt>Propagation</dt>
      <dd>Settings are cached on each server for 60 seconds. The server that handles your save uses the new values right away; other server instances pick them up within a minute. The page header says: <em>Changes propagate within a minute (server-side cache TTL).</em></dd>
      <dt>Validation</dt>
      <dd>Numbers are clamped, never rejected: minimum score to 0.50–0.99, margin to 0.00–0.50. Any mode other than <code>granular</code> is stored as <code>legacy</code>. The saved values are loaded back into the form, so after a save you see what was actually stored.</dd>
      <dt>Audit</dt>
      <dd>Only real changes are audited: <code>app_settings.rbac_mode.changed</code>, <code>app_settings.granular_gating.enabled</code> / <code>app_settings.granular_gating.disabled</code>, and <code>app_settings.import_fuzzy_match.updated</code>, each with before and after values. Saving without changes writes nothing to the audit log.</dd>
    </dl>
  </section>

  <section>
    <h2>The screen and its fields</h2>

    <h3>Per-resource grants (legacy overlay)</h3>
    <p>
      The older of the two permission systems. A single checkbox:
      <strong>Require per-resource Editor / Owner grants for workflow and integration
      writes</strong>.
    </p>
    <dl>
      <dt>Off (default)</dt>
      <dd>Per-resource roles set on a workflow's or integration's <strong>Permissions</strong> page are stored but never checked.</dd>
      <dt>On</dt>
      <dd>
        Updating a workflow or integration needs <code>editor</code> or <code>owner</code> on
        it; deleting one needs <code>owner</code>; import with <em>replace</em> or
        <em>update existing</em> needs <code>editor</code> on the target workflow. Admins
        always pass. Operators without the role get 403 <code>missing_editor_grant</code> /
        <code>missing_owner_grant</code>. Creating, reading and running are not affected.
      </dd>
    </dl>
    <p>
      When you tick the box on a deployment where it was off, a warning appears:
      <em>Existing workflows / integrations don't have grants yet. Operators may lose write
      access on save…</em>
    </p>

    <h3>RBAC mode (granular permissions)</h3>
    <p>A <strong>Mode</strong> dropdown:</p>
    <dl>
      <dt>legacy — role tiers</dt>
      <dd>Default. Each capability is decided by the user's base role (admin / operator / viewer). Permission grants are ignored.</dd>
      <dt>granular — permission grants</dt>
      <dd>Capability checks use the user's permission grants from <code>/permissions</code>. Environment and resource conditions apply when running and promoting workflows, and server/tool conditions apply to MCP tool calls. AI assistant tool calls are checked against capabilities, and messaging channels cap users by capability.</dd>
    </dl>
    <p>
      Selecting <code>granular</code> when the stored mode is <code>legacy</code> shows a
      warning: <em>Switching to granular makes permission grants authoritative. Confirm
      the grants under Permissions cover the access your operators need before
      saving.</em>
    </p>
    <Callout tone="info" title="The two switches are independent">
      The per-resource toggle and the RBAC mode can be combined in any way. The toggle
      only controls per-resource roles (owner/editor/runner/viewer); the mode only
      controls capability grants. See the precedence list in
      <a href="/docs/permissions">Permissions</a>.
    </Callout>

    <h3>Import wizard fuzzy match</h3>
    <p>
      When an imported workflow has an integration action node whose
      <code>action_name</code> doesn't exactly match (ignoring case) an action of that
      integration, the importer scores every action of the integration. The score runs
      from 0 to 1: 70% name similarity, 25% similarity to the last part of the action's
      path, 5% similarity to the start of its description. The best candidate is applied
      automatically only if it passes <strong>both</strong> thresholds:
    </p>
    <table>
      <thead><tr><th>Field</th><th>Default</th><th>Range</th><th>Effect</th></tr></thead>
      <tbody>
        <tr>
          <td><strong>Minimum score</strong></td>
          <td><code>0.80</code></td>
          <td>0.50–0.99 (step 0.05)</td>
          <td>The best candidate must score at least this. Lower means more automatic matches, and more wrong ones.</td>
        </tr>
        <tr>
          <td><strong>Margin over runner-up</strong></td>
          <td><code>0.10</code></td>
          <td>0.00–0.50 (step 0.05)</td>
          <td>The best candidate must beat the second best by at least this. Higher means the importer won't choose between near-ties. Ignored when the integration has only one action.</td>
        </tr>
      </tbody>
    </table>
    <p>
      An automatic match adds this warning to the import result:
      <code>node '&lt;node-id&gt;': action '&lt;name&gt;' fuzzy-matched to '&lt;action&gt;'
      (score 0.87). Review the node in the editor.</code> When no candidate passes, the
      node stays unresolved and the warning names the closest match if it scored at least
      0.40. You then pick the action from the node's dropdown in the editor. If two actions
      have exactly the same name, the importer never fuzzy-matches: it reports the name as
      ambiguous.
    </p>

    <h3>Footer</h3>
    <p>
      <strong>Discard</strong> puts back the stored values. <strong>Save changes</strong>
      sends all four values at once. Both buttons are disabled until you change
      something.
    </p>
  </section>

  <section>
    <h2>Main procedure</h2>
    <ol>
      <li>Open <code>/admin/settings</code> and wait for the cards to load.</li>
      <li>Change the settings you need. Read any yellow warning that appears.</li>
      <li>Click <strong>Save changes</strong>.</li>
      <li>
        If you turned <em>on</em> the per-resource toggle, a dialog opens:
        <strong>Enable per-resource grants?</strong> — <em>Operators without an explicit
        Editor or Owner grant will lose write access to existing workflows and integrations
        until you grant it under each resource's Permissions menu.</em> Click
        <strong>Enable</strong> to continue, or cancel to go back without saving
        anything.
      </li>
      <li>A <em>Settings saved</em> toast confirms. The form now shows the stored (clamped) values.</li>
    </ol>
    <Callout tone="warning" title="Only one confirmation">
      The dialog appears only when you turn the per-resource toggle on. Switching the
      RBAC mode to <code>granular</code> shows only the inline warning and saves with no
      dialog. Turning the toggle off and changing the thresholds don't ask for
      confirmation either.
    </Callout>
  </section>

  <section>
    <h2>Worked example</h2>
    <p>
      <strong>Goal:</strong> make the import wizard stricter, because your integrations
      have many similar action names (for example several <code>getX</code> variants).
    </p>
    <ol>
      <li>Open <code>/admin/settings</code>.</li>
      <li>Set <strong>Minimum score</strong> to <code>0.9</code> and <strong>Margin over runner-up</strong> to <code>0.2</code>.</li>
      <li>Click <strong>Save changes</strong>. No dialog appears.</li>
    </ol>
    <p>
      <strong>Expected result:</strong> <em>Settings saved</em>; the audit log has an
      <code>app_settings.import_fuzzy_match.updated</code> entry with the old and new
      values. Imports committed from now on auto-apply fewer actions. More nodes come back
      with <em>not found under integration</em> warnings to fix by hand. Workflows imported
      earlier are not changed.
    </p>
    <p>
      <strong>Common error:</strong> you type <code>1.2</code> as the minimum score. It is
      not rejected. After the save the field shows <code>0.99</code>, the highest allowed
      value. Enter a value in range if that's not what you wanted.
    </p>
  </section>

  <section>
    <h2>Permissions and security</h2>
    <ul>
      <li>Reading and saving settings require the <code>admin</code> role in both RBAC modes. The <code>settings.read</code> / <code>settings.manage</code> capabilities don't give non-admins access.</li>
      <li>Both authorization settings change who can do what for every user. Treat each change as a production change and check the <a href="/docs/admin/audit">audit log</a> afterwards.</li>
      <li>Neither setting can take access away from admins. Admins always pass per-resource and capability checks. The one exception is messaging channels, which cap admins too.</li>
    </ul>
  </section>

  <section>
    <h2>Impact, reverting and recovery</h2>
    <table>
      <thead><tr><th>Change</th><th>Who is affected</th><th>How to revert</th></tr></thead>
      <tbody>
        <tr>
          <td>Per-resource toggle <strong>on</strong></td>
          <td>Operators (and anyone else below admin) lose update and delete on every workflow and integration where they don't hold <code>editor</code> / <code>owner</code>.</td>
          <td>Clear the checkbox and save. No dialog.</td>
        </tr>
        <tr>
          <td>Per-resource toggle <strong>off</strong></td>
          <td>Per-resource roles stop being checked; the base role alone decides again.</td>
          <td>Tick it again and confirm the dialog.</td>
        </tr>
        <tr>
          <td>Mode → <code>granular</code></td>
          <td>Every non-admin. Built-in grants keep viewers and operators close to their old access. Custom grants take effect, and conditions start to apply when running and promoting workflows and calling MCP tools.</td>
          <td>Select <code>legacy — role tiers</code> and save. Your grants are kept.</td>
        </tr>
        <tr>
          <td>Mode → <code>legacy</code></td>
          <td>Custom grants stop counting. Users who were only allowed something through a custom grant lose it, and users limited by conditions get their role's full access back.</td>
          <td>Select <code>granular</code> and save.</td>
        </tr>
        <tr>
          <td>Fuzzy thresholds</td>
          <td>Only imports committed after the change.</td>
          <td>Enter the previous values (defaults <code>0.80</code> / <code>0.10</code>, or the before-values in the audit entry) and save.</td>
        </tr>
      </tbody>
    </table>
    <p>
      There is no reset-to-defaults button and no version history. Use the audit log to
      see earlier values. Changes can take up to a minute to reach every server.
    </p>
  </section>

  <section>
    <h2>Empty and loading states</h2>
    <ul>
      <li>While loading, a centered spinner (<em>Loading…</em>) replaces the cards.</li>
      <li>If loading fails, a red alert with the error replaces the cards, and a <em>Couldn't load settings</em> toast appears. Reload the page to retry.</li>
      <li>While saving, the inputs are disabled and <em>Save changes</em> shows a spinner.</li>
    </ul>
  </section>

  <section>
    <h2>Errors and recovery</h2>
    <table>
      <thead><tr><th>Symptom</th><th>Cause</th><th>Recovery</th></tr></thead>
      <tbody>
        <tr><td><em>Couldn't save settings</em> toast, plus a red alert in the first card</td><td>The save failed. The message comes from the server, for example <em>Too many requests. Wait a few seconds and try again.</em> when rate-limited.</td><td>Your edits are still in the form. Fix the cause and click <strong>Save changes</strong> again, or <strong>Discard</strong>.</td></tr>
        <tr><td><em>You don’t have permission to perform this action.</em></td><td>Your session isn't an admin session.</td><td>Sign in with an admin account.</td></tr>
        <tr><td>A saved number differs from what you typed</td><td>It was clamped to the allowed range.</td><td>Enter a value inside the range.</td></tr>
        <tr><td>Operators report <code>missing_editor_grant</code> / <code>missing_owner_grant</code></td><td>The per-resource toggle is on and they lack the role on that resource.</td><td>Grant <code>editor</code>/<code>owner</code> on the resource, or turn the toggle off.</td></tr>
        <tr><td>Users get <em>You don’t have permission…</em> or <code>you do not have permission to run this workflow in …</code> after switching to <code>granular</code></td><td>Their grants don't include the capability, or its conditions don't match.</td><td>Switch back to <code>legacy</code> right away, fix the grants in <a href="/docs/permissions">Permissions</a>, then switch again.</td></tr>
        <tr><td>The change seems to have no effect</td><td>Another server instance still has the old values cached.</td><td>Wait up to a minute and retry.</td></tr>
      </tbody>
    </table>
    <Callout tone="success" title="An admin cannot be locked out">
      This page, <code>/admin/users</code> and <code>/permissions</code> only check the
      <code>admin</code> role. If an admin can't do something after a change, the setting
      isn't the cause: check <a href="/docs/policies">Policies</a> or, when using chat
      from a messaging app, that channel's maximum role. If a non-admin is locked out,
      revert the setting as shown above, or temporarily change their base role in
      <a href="/docs/admin/users">Users</a>.
    </Callout>
  </section>

  <section>
    <h2>Known limitations</h2>
    <ul>
      <li>The per-resource toggle checks only update, delete and import-replace. Reading and running are never checked per resource.</li>
      <li>No dialog confirms the switch to <code>granular</code>.</li>
      <li>No reset-to-defaults button and no preview of which users would lose access.</li>
      <li>The cache is kept per server, so several instances can briefly apply different settings.</li>
      <li>The fuzzy thresholds only apply to integration action names during import. Matching for other imported dependencies is not configurable here.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/permissions">Permissions</a> — grants, conditions, per-resource roles and the role × capability matrix.</li>
      <li><a href="/docs/admin/users">Users</a> — base roles.</li>
      <li><a href="/docs/admin/audit">Audit log</a> — the history of settings changes.</li>
      <li><a href="/docs/workflows">Workflows</a> — the import wizard that uses the fuzzy thresholds.</li>
      <li><a href="/docs/admin">Admin dashboard</a> — the entry point to this page.</li>
    </ul>
  </section>
</DocLayout>
