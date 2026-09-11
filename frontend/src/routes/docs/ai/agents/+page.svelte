<script lang="ts">
    import DocLayout from "../../_components/DocLayout.svelte";
    import Callout from "../../_components/Callout.svelte";
</script>

<DocLayout
    title="AI agents"
    lead="Named roles bound to a provider with a tool allowlist, temperature, iteration cap, and optional system prompt. The default agent has role 'assistant' and is what the chat falls back to."
>
    <Callout tone="where" title="Where to find it">
        URL: <a href="/ai/agents"><code>/ai/agents</code></a>. Sidebar →
        <strong>Intelligence</strong>
        → <strong>AI</strong> → Agents card on the hub.
    </Callout>

    <section>
        <h2>Concept</h2>
        <p>
            An <strong>agent</strong> is a reusable configuration bundle the chat
            runner reads when a new conversation is opened. The same provider can
            host many agents with different model overrides, tool allowlists, temperatures,
            iteration caps, and system prompts.
        </p>
        <p>
            The chat picker shows all enabled agents; the one with
            <code>role = "assistant"</code> is the default fallback when no
            <code>agent_id</code> is sent in the request.
        </p>
        <p>
            The seed data ships with a single "Flow Weaver Assistant" agent
            bound to the first provider and the default tool list (see
            below). Feel free to rename or clone it as your team's needs grow.
        </p>
    </section>

    <section>
        <h2>Fields</h2>
        <dl>
            <dt>Name</dt>
            <dd>Display name. Shows up in the chat sidebar picker.</dd>
            <dt>Role</dt>
            <dd>
                Logical handle. <code>assistant</code> is the default; other values
                identify agents invoked by specific flows (e.g. a narrower agent
                for CI).
            </dd>
            <dt>Description</dt>
            <dd>Optional note.</dd>
            <dt>Provider</dt>
            <dd>
                Select from the enabled providers. Picks the default
                automatically when creating.
            </dd>
            <dt>Model override</dt>
            <dd>
                Model id to send instead of the provider's default. Default:
                <em>"gpt-5.5"</em>. Leave blank to inherit the provider's
                default.
            </dd>
            <dt>System prompt</dt>
            <dd>
                Optional trailing system prompt appended <em>after</em> the
                prompt skills. Skill content always precedes; this field is your
                per-agent adjustment.
            </dd>
            <dt>Tools</dt>
            <dd>
                Checkbox picker over the tool names the agent can call, loaded
                live from the server's tool registry
                (<code>GET /api/aiagent/tools</code>) — pick instead of typing,
                so typos and guessing are off the table. Includes a filter box,
                <em>Select all</em> (applies to the filtered view) and
                <em>Clear</em>. Entries saved on an agent that this server's
                registry doesn't know are kept, marked <em>not in registry</em>,
                and silently filtered out at chat time. If the catalog can't be
                loaded the form falls back to the previous free-text field.
            </dd>
            <dt>Max iterations</dt>
            <dd>
                Cap on the tool-calling loop. Default 10. Prevents runaway
                agents from calling tools forever.
            </dd>
            <dt>Temperature</dt>
            <dd>
                Float 0.0–2.0. Default 0.2. Lower → more deterministic
                responses; higher → more creative.
            </dd>
            <dt>Enabled</dt>
            <dd>
                Checkbox. Disabled agents stay in the list but don't show up in
                the picker.
            </dd>
        </dl>
    </section>

    <section>
        <h2>Default tool list</h2>
        <p>
            The "New agent" dialog pre-selects the seed tool list in the picker
            (the same one <code>DefaultAgentSeedService</code> installs):
        </p>
        <ul>
            <li><code>list_workflows</code></li>
            <li><code>list_services</code></li>
            <li><code>query_devices</code></li>
            <li><code>create_workflow_plan</code></li>
            <li><code>list_apis</code></li>
            <li><code>discover_operations</code></li>
            <li><code>operation_detail</code></li>
            <li><code>execute_operation</code></li>
            <li><code>get_run_details</code></li>
            <li><code>get_step_logs</code></li>
            <li><code>get_workflow_details</code></li>
            <li><code>update_workflow_node_config</code></li>
        </ul>
        <p>
            Uncheck tools the agent shouldn't be allowed to call. The picker only
            offers tools that actually exist on this server, so an allowlist can
            no longer drift from the registry by typo.
        </p>
    </section>

    <section>
        <h2>List page</h2>
        <p>
            Route: <code>/ai/agents</code>. Header with <em>Refresh</em> and
            <em>New agent</em>. Each row shows the name, role, provider name,
            model override, temperature, iteration cap, and enabled state.
            Actions:
            <em>Edit</em> (opens the full form), <em>Delete</em> (confirms).
        </p>
    </section>

    <section>
        <h2>Create / edit dialog</h2>
        <p>
            Same fields as <em>Fields</em> above, inside a tall modal. The
            <em>Tools</em> field is a scrollable checkbox list with a filter box,
            a selected-count badge, and <em>Select all</em> / <em>Clear</em>
            shortcuts, so long allowlists stay readable and typo-free.
        </p>
    </section>

    <section>
        <h2>How the runner resolves an agent</h2>
        <ol>
            <li>
                If the chat request carries <code>agent_id</code>, that agent is
                loaded (and must be enabled).
            </li>
            <li>
                Otherwise the runner falls back to the default agent with
                <code>role = "assistant"</code>.
            </li>
            <li>
                If none exists, the chat endpoint returns the
                <code>no_provider</code> / <code>no_agent</code> error the chat UI
                surfaces as a red alert.
            </li>
        </ol>
    </section>

    <section>
        <h2>Role differences</h2>
        <ul>
            <li><strong>Viewer</strong> — list agents.</li>
            <li><strong>Operator</strong> — full CRUD.</li>
            <li><strong>Admin</strong> — same as operator.</li>
        </ul>
    </section>

    <section>
        <h2>Related chapters</h2>
        <ul>
            <li>
                <a href="/docs/ai/providers">Providers</a> — what agents bind to.
            </li>
            <li>
                <a href="/docs/ai/skills">Skills</a> — prepended to every agent's
                system prompt.
            </li>
        </ul>
    </section>
</DocLayout>
