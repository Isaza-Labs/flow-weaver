<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="AI overview"
  lead="The Intelligence section bundles the assistant — the chat surface, the providers that serve it, the agents that configure it, the prompt skills that shape it, and the API specs it inspects."
>
  <Callout tone="where" title="Where to find it">
    Hub: <a href="/ai"><code>/ai</code></a> ·
    Chat: <code>/ai/chat</code> ·
    Providers: <code>/ai/providers</code> ·
    Agents: <code>/ai/agents</code> ·
    Skills: <code>/ai/skills</code> ·
    Specs: <code>/ai/specs</code>.
    Sidebar → <strong>Intelligence</strong> → <strong>AI</strong>.
  </Callout>

  <section>
    <h2>What the assistant is</h2>
    <p>
      FlowWeaver ships with a multi-turn, tool-calling assistant embedded into
      every screen. It can:
    </p>
    <ul>
      <li>Answer questions about your workflows, runs, devices, and services.</li>
      <li>Execute the tools listed below to gather information or make changes on your behalf.</li>
      <li>Propose and apply fixes to failing workflow steps via deep-link context from the run monitor.</li>
      <li>Author brand-new workflow plans when you describe what you need.</li>
    </ul>
    <p>
      The assistant speaks through <strong>providers</strong> (the LLM you
      brought to the platform), follows an <strong>agent</strong> profile
      (model, temperature, tool allowlist, system prompt), and is primed with
      every active <strong>prompt skill</strong> plus every loaded
      <strong>API spec</strong> on each message.
    </p>
    <p>
      The same agent is also reachable from outside the web app through
      <a href="/docs/ai/channels">messaging channels</a> (Slack, Telegram,
      WhatsApp, Teams). Permissions are enforced identically there — a channel
      can only restrict what your linked account may do, never widen it.
    </p>
  </section>

  <section>
    <h2>The hub page</h2>
    <p>
      Route: <code>/ai</code>. Landing page for the Intelligence section.
      Header action: <em>Open chat</em> (sparkles icon) jumps directly to
      <code>/ai/chat</code>.
    </p>

    <h3>Stat row</h3>
    <dl>
      <dt>Conversations</dt><dd>Number of conversations in the preview list (not the total count). Card links to <code>/ai/chat</code>.</dd>
      <dt>Providers</dt><dd>Total LLM providers configured. Card links to <code>/ai/providers</code>.</dd>
      <dt>Agents</dt><dd>Total agent profiles. Card links to <code>/ai/agents</code>.</dd>
    </dl>

    <h3>Three-column body</h3>
    <p>
      Below the stats, three cards laid out side-by-side on wide screens:
    </p>

    <h4>Recent conversations</h4>
    <p>
      Up to five recent conversations with titles (derived from the first
      user message), message count, and relative time. Each row links to
      <code>/ai/chat?conversation={'{id}'}</code> which opens that thread
      directly. A <em>New</em> button in the header opens the chat with no
      preselected conversation. Empty state: <em>"No conversations yet."</em>
    </p>

    <h4>Agent composition</h4>
    <p>
      Quick links to the three components that shape every chat:
    </p>
    <ul>
      <li><strong>Prompt skills</strong> → <a href="/ai/skills"><code>/ai/skills</code></a>.</li>
      <li><strong>API specs</strong> → <a href="/ai/specs"><code>/ai/specs</code></a>.</li>
      <li><strong>Secrets (admin)</strong> → <a href="/admin/secrets"><code>/admin/secrets</code></a> (admin-only).</li>
    </ul>
    <p>
      The text above the links reminds you: <em>"The default assistant
      combines your prompt skills with every tool below. Edit them anytime —
      the agent picks up changes on the next message."</em>
    </p>

    <h4>Built-in tools</h4>
    <p>
      A read-only list of the tools the agent can call out of the box,
      grouped visually with a small wrench icon. Exact names:
    </p>
    <ul>
      <li><code>list_apis</code> · <code>discover_operations</code></li>
      <li><code>operation_detail</code> · <code>execute_operation</code></li>
      <li><code>get_run_details</code> · <code>get_step_logs</code></li>
      <li><code>get_workflow_details</code> · <code>update_workflow_node_config</code></li>
      <li><code>list_workflows</code> · <code>list_services</code> · <code>query_devices</code></li>
      <li><code>create_workflow_plan</code></li>
    </ul>
    <p>
      Permission is enforced per caller role at tool-dispatch time — a viewer
      asking the agent to call <code>update_workflow_node_config</code> will
      get a 403 at the tool boundary even though the tool is available in
      principle.
    </p>
  </section>

  <section>
    <h2>How a chat call assembles</h2>
    <p>
      When you hit <em>Send</em> in <code>/ai/chat</code>, the backend:
    </p>
    <ol>
      <li>
        Picks the active agent (either the <code>agent_id</code> you
        selected in the sidebar or the default fallback with
        <code>Role = "assistant"</code>).
      </li>
      <li>
        Looks up the agent's provider, resolves the API key from encrypted
        storage, and starts a session at the chosen <code>default_model</code>
        (overridden by the agent's <code>modelOverride</code> if set).
      </li>
      <li>
        Concatenates the system prompt from: the agent's own prompt +
        every active <strong>prompt skill</strong> (sorted by
        <code>sort_order</code> then name) with <code>{'{{CurrentDate}}'}</code>
        and <code>{'{{ToolList}}'}</code> placeholders resolved.
      </li>
      <li>
        Populates the tool catalogue from the agent's allowlist intersected
        with what the caller's role can actually invoke. Loads every active
        <strong>API spec</strong> so <code>discover_operations</code> can
        reference them.
      </li>
      <li>
        Streams deltas back through SSE. Tool calls are rendered as
        collapsible cards; text deltas are accumulated character-by-character.
      </li>
    </ol>
  </section>

  <section>
    <h2>Role differences</h2>
    <ul>
      <li>
        <strong>Viewer</strong> — can read the hub, open the chat, and send
        messages. Tool calls that mutate state (e.g.
        <code>update_workflow_node_config</code>, any
        <code>execute_operation</code> against a destructive endpoint) return
        a 403 at dispatch time.
      </li>
      <li><strong>Operator</strong> — full read + write tool access.</li>
      <li><strong>Admin</strong> — same as operator, plus the
        <em>Clear all</em> button in the chat sidebar for bulk-deleting
        conversations.</li>
    </ul>
  </section>

  <section>
    <h2>Related chapters</h2>
    <ul>
      <li><a href="/docs/ai/chat">Chat</a> — the full conversation UI.</li>
      <li><a href="/docs/ai/channels">Messaging channels</a> — the agent from Slack / Telegram / WhatsApp / Teams.</li>
      <li><a href="/docs/ai/providers">Providers</a> — connecting to LLMs.</li>
      <li><a href="/docs/ai/agents">Agents</a> — configuring the profile.</li>
      <li><a href="/docs/ai/skills">Skills</a> — prompt snippets injected into every turn.</li>
      <li><a href="/docs/ai/specs">API specs</a> — OpenAPI documents the agent can inspect.</li>
    </ul>
  </section>
</DocLayout>
