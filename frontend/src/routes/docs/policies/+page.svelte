<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Policies"
  lead="Global guardrails evaluated on every workflow create, update, and promotion, and on each SSH command at run time. Authoring needs policy.manage; audit trail of every block."
>
  <Callout tone="where" title="Where to find it">
    List: <a href="/policies"><code>/policies</code></a> ·
    Audit: <code>/policies/audit</code>.
    Sidebar → <strong>Govern</strong> → <strong>Policies</strong> (admins only).
  </Callout>
  <Callout tone="admin" title="Who can do what">
    Reading policies and the audit page needs <code>policy.read</code>, which
    every role holds. Creating, editing, toggling and deleting needs
    <code>policy.manage</code> (admin by default, or granted with granular
    permissions). The <strong>Govern</strong> sidebar group is shown to admins
    only; other roles open <code>/policies</code> directly.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A <strong>policy</strong> is a rule with an <em>action</em> (deny),
      a <em>reason</em>, and a <em>when</em> clause that describes the
      operations it applies to. The policy engine runs every enabled rule
      against each workflow-level write (create, update, promote) and blocks
      the operation if a match is found.
    </p>
    <p>
      Typical uses:
    </p>
    <ul>
      <li>Deny creating workflows with raw <code>ssh</code> snippets in the <code>production</code> environment.</li>
      <li>Require a description containing a change-ticket id before any production promotion.</li>
      <li>Forbid operating on a specific device pool unless the operator's role is above a threshold.</li>
    </ul>
  </section>

  <section>
    <h2>Rule shape</h2>
    <p>
      Stored as JSON. The top-level fields are:
    </p>
    <dl>
      <dt>action</dt><dd><code>deny</code> (block when <code>when</code> matches) or <code>gate</code> (block a promotion until requirements are met — see below).</dd>
      <dt>reason</dt><dd>Human-readable explanation surfaced in the error the caller receives.</dd>
      <dt>when</dt>
      <dd>
        Object with any combination of the matchers below. Empty
        <code>when</code> matches every operation (effectively a block-all
        rule).
      </dd>
    </dl>
    <table>
      <thead><tr><th>Matcher</th><th>Matches</th></tr></thead>
      <tbody>
        <tr><td><code>env</code></td><td>Array of environments (<code>draft</code>, <code>qa</code>, <code>production</code>).</td></tr>
        <tr><td><code>device_role</code></td><td>Array of device role strings.</td></tr>
        <tr><td><code>device_pool</code></td><td>Array of pool ids.</td></tr>
        <tr><td><code>snippet_type</code></td><td>Array of snippet types (<code>python_snippet</code>, <code>ssh</code>, <code>ansible_playbook</code>, …).</td></tr>
        <tr><td><code>description_contains</code></td><td>Substring that must / must not appear in the workflow description.</td></tr>
        <tr><td><code>action</code></td><td>The operation being attempted (<code>create</code>, <code>update</code>, <code>promote</code>, or <code>ssh_exec</code> for a single SSH command at run time).</td></tr>
        <tr><td><code>ssh_command_regex</code></td><td>Array of regular expressions (case-insensitive) matched against each command an <code>ssh</code> step is about to send. A match denies that command: the step stops there and the command never reaches the device. This is the only mechanism that <em>blocks</em> CLI commands — the <a href="/docs/vendor-commands">vendor command catalog</a> only warns. A pattern that does not compile or takes longer than 250 ms counts as no match.</td></tr>
      </tbody>
    </table>
    <p>Example — never let an SSH step reload a device in production:</p>
    <pre><code>{`{
  "action": "deny",
  "reason": "reload is not allowed in production",
  "when": { "action": ["ssh_exec"], "env": ["production"], "ssh_command_regex": ["^\\\\s*reload\\\\b"] }
}`}</code></pre>

    <h3>Gate rules</h3>
    <p>
      A <code>gate</code> rule applies to promotions (<code>"on": "promote"</code>),
      optionally only for a given source (<code>from</code>) and target
      (<code>to</code>) environment, and denies the promotion when any entry in
      <code>require</code> is not met. The error lists every unmet requirement.
    </p>
    <dl>
      <dt>successful_runs</dt><dd>At least <code>min</code> successful runs within <code>within_days</code> (0 = any time). <code>scope</code>: <code>this_workflow</code> (default) or <code>any_workflow</code>.</dd>
      <dt>last_successful_run_within</dt><dd>The last successful run happened within <code>days</code>. Same <code>scope</code> options.</dd>
      <dt>successful_snippet_runs</dt><dd>At least <code>min</code> successful runs of the listed <code>snippet_ids</code> (any snippet when empty) within <code>within_days</code>.</dd>
    </dl>
    <pre><code>{`{
  "action": "gate",
  "on": "promote",
  "from": "qa",
  "to": "production",
  "reason": "qa validation required",
  "require": [
    { "type": "successful_runs", "min": 1, "within_days": 7, "scope": "this_workflow" }
  ]
}`}</code></pre>
    <p>
      The visual builder covers both shapes: the <code>deny</code> matchers
      (including <code>ssh_command_regex</code>) and gate rules with their
      <code>require</code> list.
    </p>
    <p>
      On first start FlowWeaver seeds <code>default.qa_to_production</code>, a
      gate that requires a successful run of the same workflow within the last
      2 days (<code>last_successful_run_within</code>). It is the 48-hour rule
      shown on the <a href="/docs/qa-lab">QA lab</a> page and can be edited like
      any other policy.
    </p>
  </section>

  <section>
    <h2>List page</h2>
    <p>
      Route: <code>/policies</code>. Header actions: <em>Audit</em>
      (link to <code>/policies/audit</code>) and <em>New policy</em>.
    </p>
    <p>
      Each policy is a card with its name, an <em>enabled / disabled</em>
      badge, and the description. The right-hand side has a power toggle to
      enable/disable in place and a red delete button.
    </p>
    <p>
      Clicking the name expands the card into an inline edit form (see below).
    </p>
  </section>

  <section>
    <h2>Create form</h2>
    <p>
      Opens from the <em>New policy</em> button. Fields:
    </p>
    <dl>
      <dt>Name</dt><dd>Required. Conventionally kebab-case, like <code>no-ssh-in-production</code>.</dd>
      <dt>Enabled immediately</dt><dd>Checkbox. When off, the policy is created in a parked state for later review.</dd>
      <dt>Description (optional)</dt><dd>Free-text summary.</dd>
      <dt>Rule</dt>
      <dd>
        The meat of the policy. Authored in one of two modes (tabs on top
        of the rule area):
        <ul>
          <li>
            <strong>Visual</strong> — a chip-based builder that composes the JSON for you.
            Each matcher gets a chip list you can add/remove values from.
          </li>
          <li>
            <strong>JSON</strong> — a raw JSON textarea for power users.
            The helper text under the input reminds you of the shape.
          </li>
        </ul>
      </dd>
    </dl>
    <p>
      Both modes are backed by the same string, so you can switch back and
      forth without losing state. The visual builder shows a warning alert if
      the current JSON can't be parsed into its chip model.
    </p>
    <p>
      On success the form resets, closes, and the list refreshes.
    </p>
  </section>

  <section>
    <h2>Edit form</h2>
    <p>
      Expanding a policy card reveals the same field set as create, pre-populated.
      The <em>Save changes</em> button is bottom-right; no confirmation.
    </p>
    <Callout tone="info" title="Existing workflows are not re-checked">
      Enabling or editing a policy only affects <em>new</em> create / update /
      promote operations going forward. Already-saved workflows are not
      re-validated retroactively.
    </Callout>
  </section>

  <section>
    <h2>Enable / disable toggle</h2>
    <p>
      The power icon on the right of each card flips <code>enabled</code>.
      Disabled policies stay in the list (easy to re-arm) but the engine skips
      them at evaluation time.
    </p>
  </section>

  <section>
    <h2>Delete</h2>
    <p>
      Confirmation dialog warns that existing workflows are unaffected, but
      new creates/runs will stop being blocked by the rule. The policy row
      disappears on success.
    </p>
  </section>

  <section>
    <h2>Audit page</h2>
    <p>
      Route: <code>/policies/audit</code>. Every time a policy blocks an
      operation a trace event is emitted. This page rolls those up so you can
      tell which rules are doing real work and which are noise.
    </p>

    <h3>Window selector</h3>
    <p>
      A <em>Select</em> at the top controls the reporting window:
      <code>last 24h</code>, <code>last 7 days</code> (default),
      <code>last 30 days</code>, <code>last 90 days</code>. Changing the
      value re-fetches automatically.
    </p>

    <h3>Cards</h3>
    <dl>
      <dt>Blocks</dt><dd>Total number of blocked operations in the selected window.</dd>
      <dt>Unique policies firing</dt><dd>How many distinct rules were responsible.</dd>
      <dt>Noisiest rule</dt><dd>Which single policy produced the most blocks, with a count.</dd>
    </dl>

    <h3>Top-offenders row</h3>
    <p>
      Below the cards, a horizontal list of the top 8 policy names by block
      count gives a quick read on where the attrition is concentrated.
    </p>

    <h3>Recent blocks</h3>
    <p>
      A table of individual block events with: timestamp, operator, operation
      (<code>create</code>/<code>update</code>/<code>promote</code>), the
      workflow affected, which policy fired, and the captured reason.
    </p>
    <Callout tone="success" title="Tuning tip">
      If a rule consistently appears as the noisiest offender but never
      represents a real security event, it's probably over-matching. Soften
      the <code>when</code> clause (narrower env, specific snippet_type) or
      disable it.
    </Callout>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li><strong>Viewer / operator</strong> — read policies and the audit page (<code>policy.read</code>). Create, edit, toggle and delete are rejected unless the user also holds <code>policy.manage</code>.</li>
      <li><strong>Admin</strong> — full access: CRUD, toggle, audit.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/workflows">Workflows</a> — the surface policies gate.</li>
      <li><a href="/docs/admin/traces">Traces</a> — every policy block is also logged here as a trace event.</li>
    </ul>
  </section>
</DocLayout>
