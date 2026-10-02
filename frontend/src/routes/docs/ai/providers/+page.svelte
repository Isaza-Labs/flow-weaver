<script lang="ts">
    import DocLayout from "../../_components/DocLayout.svelte";
    import Callout from "../../_components/Callout.svelte";
</script>

<DocLayout
    title="AI providers"
    lead="LLM providers your assistant can talk to. Configure OpenAI, Anthropic, Google Gemini, DeepSeek, Kimi, Ollama — or any other OpenAI-compatible endpoint — with a single encrypted API key and a default model."
>
    <Callout tone="where" title="Where to find it">
        URL: <a href="/ai/providers"><code>/ai/providers</code></a>. Sidebar →
        <strong>Intelligence</strong>
        → <strong>AI</strong> → Providers card on the hub.
    </Callout>

    <section>
        <h2>Concept</h2>
        <p>
            A <strong>provider</strong> is a connection to an LLM service. It holds:
        </p>
        <ul>
            <li>A display name.</li>
            <li>
                A type (<code>openai</code>, <code>anthropic</code>,
                <code>gemini</code>, <code>deepseek</code>, <code>kimi</code>,
                <code>ollama</code>, <code>custom</code>).
            </li>
            <li>An optional base URL (useful for self-hosted proxies).</li>
            <li>A default model id (e.g. <code>gpt-5.5</code>).</li>
            <li>An encrypted API key.</li>
            <li>An <em>enabled</em> flag.</li>
        </ul>
        <Callout tone="info" title="Key privacy">
            API keys are encrypted server-side by the credential encryption
            service. The list response never echoes them back. Editing a row
            lets you overwrite the stored value but never reveals the current
            one — a cleartext field with no placeholder means "leave blank to
            keep current".
        </Callout>
    </section>

    <section>
        <h2>List page</h2>
        <p>
            Route: <code>/ai/providers</code>. Header with <em>Refresh</em> and
            <em>New provider</em> buttons. Large spinner during initial load; an
            empty-state card with a <em>Plug</em> icon when none exist.
        </p>

        <h3>Row</h3>
        <p>
            Each row carries the provider name, type badge, default model,
            updated timestamp, enabled badge, and action buttons.
        </p>

        <h3>Actions per row</h3>
        <dl>
            <dt>Test</dt>
            <dd>
                Fires a one-shot call to the provider using the stored key.
                Per-row status appears inline while testing (spinner), then a
                check-mark (OK) or cross (failed) icon. A toast holds the
                full error on failure.
            </dd>
            <dt>Edit</dt>
            <dd>Opens the form dialog pre-populated (except the API key).</dd>
            <dt>Delete</dt>
            <dd>
                Confirms then removes. Any agent bound to this provider will
                lose its LLM — the chat surface will report <em
                    >"No AI provider is configured yet"</em
                > until you rebind.
            </dd>
        </dl>
    </section>

    <section>
        <h2>Create / edit dialog</h2>
        <p>
            Opened by <em>New provider</em> or any row's pencil button.
        </p>

        <h3>Fields</h3>
        <dl>
            <dt>Name</dt>
            <dd>Display name. Default: <em>"OpenAI"</em> when creating.</dd>
            <dt>Type</dt>
            <dd>
                Select: <code>openai</code>, <code>anthropic</code>,
                <code>gemini</code>, <code>deepseek</code>, <code>kimi</code>,
                <code>ollama</code>, or <code>custom</code> for any other
                OpenAI-compatible endpoint. Drives the auth shape and the adapter
                on the backend, and is rejected if it is not one of these.
            </dd>
            <dt>Base URL</dt>
            <dd>
                Optional for every named type — leave it empty to use that
                provider's public endpoint (<code
                    >https://generativelanguage.googleapis.com</code
                > for Gemini, <code>https://api.deepseek.com</code> for DeepSeek).
                <strong>Required</strong> for <code>custom</code>, which has no
                vendor endpoint to fall back to. Override it for self-hosted
                proxies, local Ollama (<code>http://localhost:11434</code>), or
                Azure-hosted OpenAI. Pasting the vendor's documented base URL with
                its version on it (<code>https://api.deepseek.com/v1</code>) is
                fine: the version is not appended twice.
            </dd>
            <dt>Default model</dt>
            <dd>
                Model id the provider will route to when agents don't override
                it. Default <em>"gpt-5.5"</em> on create.
            </dd>
            <dt>API key</dt>
            <dd>
                Masked input. Required when creating. On edit, empty means "keep
                existing"; a non-empty value rotates the stored key.
            </dd>
            <dt>Workspace ID</dt>
            <dd>
                Anthropic only, and optional. Names the workspace the requests
                bill and are scoped to, sent as the
                <code>anthropic-workspace-id</code> header. Required when the API
                key is <em>identity-linked</em> — minted from your own account
                rather than inside a workspace — because such a key can reach
                several workspaces and the API refuses to guess; without it every
                turn fails with <em
                    >"anthropic-workspace-id is required when authenticating with
                    an identity-linked API key"</em
                >. A key created inside a workspace carries its own and needs
                none. It is the opaque <code>wrkspc_…</code> segment of the
                workspace URL in the Anthropic Console, not the workspace name.
            </dd>
            <dt>Enabled</dt>
            <dd>
                Checkbox. Disabled providers are ignored by the chat router.
            </dd>
        </dl>

        <h3>Validation</h3>
        <ul>
            <li>Name, type, and default model are always required.</li>
            <li>On create the API key is required.</li>
            <li>
                A workspace ID that is not a <code>wrkspc_…</code> handle is
                rejected by the form, so a pasted Console URL or workspace name
                is caught here rather than on the next chat turn.
            </li>
            <li>
                Invalid keys surface as 4xx errors from the provider when you
                test the row.
            </li>
        </ul>
    </section>

    <section>
        <h2>Role differences</h2>
        <ul>
            <li>
                <strong>Viewer</strong> — see provider metadata (never keys).
            </li>
            <li><strong>Operator</strong> — same as viewer.</li>
            <li><strong>Admin</strong> — create, edit, delete and test (<code>aiprovider.manage</code>, because a provider holds an API key).</li>
        </ul>
    </section>

    <section>
        <h2>Related chapters</h2>
        <ul>
            <li>
                <a href="/docs/ai/agents">Agents</a> — bind to a provider here.
            </li>
            <li>
                <a href="/docs/ai/chat">Chat</a> — the surface that consumes providers.
            </li>
        </ul>
    </section>
</DocLayout>
