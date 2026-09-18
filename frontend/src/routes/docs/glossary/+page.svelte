<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Glossary"
  lead="The words this manual uses, each with one meaning. When a chapter says 'run', 'target' or 'action', it means exactly what is written here."
>
  <Callout tone="where" title="Where to find it">
    Manual → <strong>Reference</strong> → <strong>Glossary</strong>. Terms are
    alphabetical; each links to the chapter that covers it in depth.
  </Callout>

  <section>
    <h2>Terms</h2>
    <dl>
      <dt>action</dt>
      <dd>
        One HTTP endpoint on an <em>integration</em> (method + path), discovered
        from an OpenAPI spec or registered by hand. Dropped onto a workflow it
        becomes an <code>integration_action</code> node. See
        <a href="/docs/integrations">Integrations</a>.
      </dd>
      <dt>agent</dt>
      <dd>
        A named assistant profile: provider, model, tool allowlist, temperature,
        iteration cap and system prompt. See <a href="/docs/ai/agents">Agents</a>.
      </dd>
      <dt>artifact</dt>
      <dd>
        A generated document (HTML, CSV, XLSX, PDF). A <em>report</em> comes from a
        workflow run; an <em>export</em> from the agent or a direct API call. See
        <a href="/docs/admin/artifacts">Artifacts</a>.
      </dd>
      <dt>audit event</dt>
      <dd>
        A record of a meaningful write (domain event) or an authentication event.
        See <a href="/docs/admin/audit">Audit log</a>.
      </dd>
      <dt>bundle</dt>
      <dd>
        The portable export format: a JSON document carrying each referenced
        snippet's definition and a stable slug for every integration and action,
        so another FlowWeaver instance can bind it. Never includes credentials.
        See <a href="/docs/workflows/import">Importing workflows</a>.
      </dd>
      <dt>capability</dt>
      <dd>
        A named permission such as <code>mcp.execute</code> or
        <code>report.manage</code>. Grants confer capabilities on users. See
        <a href="/docs/permissions">Permissions</a>.
      </dd>
      <dt>config_overrides</dt>
      <dd>
        The per-node settings of a workflow node. They customise a snippet for
        that node without changing the snippet, and may contain
        <code>{'{{ … }}'}</code> templates.
      </dd>
      <dt>credential</dt>
      <dd>
        Username plus password or SSH private key that a <em>device</em> points at
        so handlers can log in. See <a href="/docs/credentials">Credentials</a>.
      </dd>
      <dt>device</dt>
      <dd>
        A managed host (router, switch, firewall, server) a workflow can execute
        against. See <a href="/docs/devices">Devices</a>.
      </dd>
      <dt>device pool</dt>
      <dd>
        A named group of devices that a run or schedule can target as one. See
        <a href="/docs/device-pools">Device pools</a>.
      </dd>
      <dt>edge</dt>
      <dd>
        A connection between two nodes. Its type — <code>success</code>,
        <code>failure</code>, <code>always</code> or <code>conditional</code> —
        decides when the target node runs.
      </dd>
      <dt>environment</dt>
      <dd>
        The lifecycle stage of a workflow: <code>draft</code>, <code>qa</code> or
        <code>production</code>. Devices and pools declare which environments may
        reach them. See <a href="/docs/qa-lab">QA lab</a>.
      </dd>
      <dt>integration</dt>
      <dd>
        A connection to an external REST system: base URL, authentication and a
        catalogue of actions. See <a href="/docs/integrations">Integrations</a>.
      </dd>
      <dt>MCP server</dt>
      <dd>
        An external Model Context Protocol server whose tools the agent and the
        <code>mcp_call</code> node can call. See
        <a href="/docs/admin/mcp-servers">MCP servers</a>.
      </dd>
      <dt>messaging channel</dt>
      <dd>
        A Slack, Telegram, WhatsApp or Teams binding that lets users talk to the
        agent outside the web app. See <a href="/docs/ai/channels">Messaging channels</a>.
      </dd>
      <dt>node</dt>
      <dd>
        One step in a workflow graph. It calls a snippet, an integration action,
        an MCP tool or a subflow. <em>Start</em> and <em>End</em> are
        <em>sentinel</em> nodes.
      </dd>
      <dt>policy</dt>
      <dd>
        A global deny rule evaluated on workflow create, update and promote. See
        <a href="/docs/policies">Policies</a>.
      </dd>
      <dt>promotion</dt>
      <dd>
        Moving a workflow to the next environment (<code>draft → qa →
        production</code>). Production requires a recent successful QA run and a
        second approver. See <a href="/docs/workflows">Workflows</a>.
      </dd>
      <dt>provider</dt>
      <dd>
        A connection to an LLM service (OpenAI, Anthropic, Gemini, …). See
        <a href="/docs/ai/providers">Providers</a>.
      </dd>
      <dt>role</dt>
      <dd>
        The base access level of a user: <code>viewer</code>,
        <code>operator</code> or <code>admin</code>. See
        <a href="/docs/admin/users">Users</a>.
      </dd>
      <dt>rollback</dt>
      <dd>
        Restoring an earlier production version of a workflow. Refused when the
        graph contains a non-reversible step such as <code>email_send</code>.
      </dd>
      <dt>run</dt>
      <dd>
        One execution of a workflow, with a frozen snapshot of its graph. See
        <a href="/docs/runs">Runs</a>.
      </dd>
      <dt>schedule</dt>
      <dd>
        A trigger that fires a workflow from a cron expression and timezone. See
        <a href="/docs/schedules">Schedules</a>.
      </dd>
      <dt>secret</dt>
      <dd>
        An encrypted value stored centrally and referenced by name instead of
        pasting it into code or config. See <a href="/docs/admin/secrets">Secrets</a>.
      </dd>
      <dt>sentinel</dt>
      <dd>
        The fixed <code>__start__</code> and <code>__end__</code> nodes every
        workflow has. They cannot be renamed or deleted.
      </dd>
      <dt>skill</dt>
      <dd>
        A <em>prompt skill</em>: a Markdown file added to the agent's system
        prompt. Not to be confused with the legacy <em>reusable actions</em> table.
        See <a href="/docs/ai/skills">Prompt skills</a>.
      </dd>
      <dt>SLO</dt>
      <dd>
        Service-level objective: a target for a platform indicator such as
        latency or error rate over a time window. See
        <a href="/docs/admin/slo">Service-level objectives</a>.
      </dd>
      <dt>snippet</dt>
      <dd>
        A reusable unit of work (Python, Ansible, REST call, transform, ping, …)
        that a node invokes. See <a href="/docs/snippets">Snippets</a>.
      </dd>
      <dt>spec</dt>
      <dd>
        An OpenAPI YAML document the agent can inspect and call through its
        dynamic tools. See <a href="/docs/ai/specs">API specs</a>.
      </dd>
      <dt>step run</dt>
      <dd>
        The record of one node execution inside a run (one per device for
        <code>per_device</code> nodes): status, input, output, logs.
      </dd>
      <dt>subflow</dt>
      <dd>
        A workflow called as a single node from another workflow. See
        <a href="/docs/subflows">Subflows</a>.
      </dd>
      <dt>target</dt>
      <dd>
        A device a run executes against, chosen directly or through a pool, and
        filtered by the workflow's environment.
      </dd>
      <dt>target mode</dt>
      <dd>
        How often a snippet executes in a run: <code>per_device</code>,
        <code>once</code> or <code>per_pool</code>.
      </dd>
      <dt>tool</dt>
      <dd>
        A function the agent can call (<code>list_workflows</code>,
        <code>find_command</code>, an MCP tool, …). Every call is checked against
        the caller's permissions.
      </dd>
      <dt>trace</dt>
      <dd>
        A record of one handler invocation (including reads and tool calls), used
        for debugging. See <a href="/docs/admin/traces">Traces</a>.
      </dd>
      <dt>trigger</dt>
      <dd>
        Anything that starts a run automatically: a <em>schedule</em> or a
        <em>webhook</em>. See <a href="/docs/workflows/triggers">Triggers and webhooks</a>.
      </dd>
      <dt>vendor command</dt>
      <dd>
        A catalogue entry describing a CLI command for a device platform, used to
        find and validate SSH commands. See
        <a href="/docs/vendor-commands">Vendor commands</a>.
      </dd>
      <dt>webhook</dt>
      <dd>
        A trigger that starts a run when an external system POSTs a signed request.
      </dd>
      <dt>workflow</dt>
      <dd>
        A directed graph of nodes and edges that automates a task. See
        <a href="/docs/workflows">Workflows</a>.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/reference">Reference</a> — every screen, its route and minimum role.</li>
      <li><a href="/docs/troubleshooting">Troubleshooting</a> — find the fix by symptom.</li>
    </ul>
  </section>
</DocLayout>
