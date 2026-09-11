<script lang="ts">
    // CRUD for AI providers (OpenAI / Anthropic / Gemini / DeepSeek / Kimi /
    // Ollama / any other OpenAI-compatible endpoint via `custom`). The API key is
    // encrypted server-side via ICredentialEncryptionService — the list
    // response never echoes it back, so editing a row lets you overwrite it
    // but never reveal it.

    import { onMount } from "svelte";
    import {
        aiProviders,
        type AIProvider,
        errorMessage,
    } from "$lib/api/client";
    import {
        PageHeader,
        Card,
        Button,
        Input,
        Select,
        Dialog,
        Alert,
        Spinner,
        EmptyState,
        StatusBadge,
        formatDateTime,
        toast,
        confirm,
        FieldHint,
    } from "$lib/components/ui";
    import {
        Plug,
        Plus,
        Pencil,
        Trash2,
        RefreshCw,
        CheckCircle2,
        XCircle,
        Loader2,
    } from "lucide-svelte";

    let rows = $state<AIProvider[]>([]);
    let loading = $state(true);
    let loadError = $state<string | null>(null);

    let showDialog = $state(false);
    let editing = $state<AIProvider | null>(null);
    let name = $state("");
    let type = $state<
        "openai" | "anthropic" | "gemini" | "deepseek" | "kimi" | "ollama" | "custom"
    >("openai");
    let baseUrl = $state("");
    let defaultModel = $state("gpt-5.5");
    // Optional Config.model_limits. Empty means "leave the vendor default":
    // Anthropic/Gemini 8192 output tokens, OpenAI the model's own maximum,
    // Ollama the model's default window (which is small — set it).
    let contextWindow = $state("");
    let maxOutputTokens = $state("");
    // Optional Config.workspace_id, Anthropic only. Empty means "the key carries
    // its own workspace", which is true of every key created inside one.
    let workspaceId = $state("");
    let apiKey = $state("");
    let enabled = $state(true);
    let saving = $state(false);
    let formError = $state<string | null>(null);

    // Per-row test state so the button can show spinner + result without
    // clobbering a concurrent test on another provider.
    let testing = $state<Record<string, "running" | "ok" | "failed">>({});

    onMount(load);

    async function load() {
        loading = true;
        loadError = null;
        try {
            const res = await aiProviders.list(100, 0);
            rows = res.data;
        } catch (e) {
            loadError = errorMessage(e);
            toast.fromError(e, "Couldn't load providers");
        } finally {
            loading = false;
        }
    }

    function openCreate() {
        editing = null;
        name = "OpenAI";
        type = "openai";
        baseUrl = "";
        defaultModel = "gpt-5.5";
        contextWindow = "";
        maxOutputTokens = "";
        workspaceId = "";
        apiKey = "";
        enabled = true;
        formError = null;
        showDialog = true;
    }

    function openEdit(p: AIProvider) {
        editing = p;
        name = p.name;
        type = p.type as typeof type;
        baseUrl = p.base_url ?? "";
        defaultModel = p.default_model;
        const limits = (p.config?.model_limits ?? {}) as Record<string, unknown>;
        contextWindow = typeof limits.context_window === "number" ? String(limits.context_window) : "";
        maxOutputTokens = typeof limits.max_output_tokens === "number" ? String(limits.max_output_tokens) : "";
        const cfg = (p.config ?? {}) as Record<string, unknown>;
        workspaceId = typeof cfg.workspace_id === "string" ? cfg.workspace_id : "";
        apiKey = "";
        enabled = p.enabled;
        formError = null;
        showDialog = true;
    }

    function closeDialog() {
        if (saving) return;
        showDialog = false;
        editing = null;
    }

    function validate(): string | null {
        if (!name.trim()) return "Name is required.";
        if (!defaultModel.trim()) return "Default model is required.";
        if (!editing && type !== "ollama" && type !== "custom" && !apiKey.trim())
            return "API key is required for new providers.";
        if (type === "ollama" && !baseUrl.trim())
            return "Ollama needs a base URL (e.g. http://host.docker.internal:11434).";
        if (type === "custom" && !baseUrl.trim())
            return "A custom provider needs the base URL of its OpenAI-compatible endpoint.";
        const cw = parseLimit(contextWindow);
        const mo = parseLimit(maxOutputTokens);
        if (cw === false) return "Context window must be a whole number of tokens (at least 2048).";
        if (mo === false) return "Max output tokens must be a whole number of tokens (at least 256).";
        if (cw !== null && mo !== null && mo >= cw)
            return "Max output tokens must be smaller than the context window.";
        // The workspace id is an opaque `wrkspc_…` handle that only appears in the
        // Console URL, and the two things people reach for instead — the workspace
        // name and the whole URL — would both be accepted here, saved, and then
        // rejected by Anthropic one chat turn later with "must be a valid workspace
        // ID". Naming the mistake here is the difference between a five-second fix
        // and a hunt through the logs.
        const ws = workspaceId.trim();
        if (type === "anthropic" && ws && !WORKSPACE_ID.test(ws)) {
            // An id buried in a longer string is a pasted URL; anything else —
            // including a bare `wrkspc_` — is someone who does not have the id yet.
            const embedded = ws.match(WORKSPACE_ID_ANYWHERE);
            return embedded
                ? `Workspace ID looks like a URL — paste only ${embedded[0]}.`
                : "Workspace ID is the id, not the workspace name. Open the workspace in the Anthropic Console; the id is the wrkspc_… segment of the URL.";
        }
        return null;
    }

    // Console workspace handles are `wrkspc_` followed by an opaque token.
    const WORKSPACE_ID = /^wrkspc_[A-Za-z0-9_-]+$/;
    const WORKSPACE_ID_ANYWHERE = /wrkspc_[A-Za-z0-9_-]+/;

    // "" → null (not set); a positive integer → the number; anything else → false.
    function parseLimit(raw: string): number | null | false {
        const s = raw.trim();
        if (!s) return null;
        if (!/^\d+$/.test(s)) return false;
        const n = Number(s);
        return n > 0 ? n : false;
    }

    // The whole Config object as it will be stored: the keys this form owns,
    // rewritten from the fields, over whatever else the row already carried —
    // the backend replaces Config wholesale, so anything dropped here is lost.
    function buildConfig(): Record<string, unknown> {
        const base = { ...(editing?.config ?? {}) } as Record<string, unknown>;
        const cw = parseLimit(contextWindow);
        const mo = parseLimit(maxOutputTokens);
        if (cw === null && mo === null) {
            delete base.model_limits;
        } else {
            const limits: Record<string, number> = {};
            if (typeof cw === "number") limits.context_window = cw;
            if (typeof mo === "number") limits.max_output_tokens = mo;
            base.model_limits = limits;
        }

        // Only Anthropic reads it, and only the field the user can see decides:
        // clearing the box has to actually remove the key, or the header would
        // keep being sent with a workspace nobody can see any more.
        const ws = workspaceId.trim();
        if (type === "anthropic" && ws) base.workspace_id = ws;
        else if (type === "anthropic") delete base.workspace_id;

        return base;
    }

    async function save() {
        if (saving) return;
        formError = validate();
        if (formError) return;

        saving = true;
        try {
            const body: Partial<AIProvider> & { api_key?: string } = {
                name: name.trim(),
                type,
                default_model: defaultModel.trim(),
                base_url: baseUrl.trim() || null,
                enabled,
                config: buildConfig(),
            };
            // Only send the key when it was set — editing with an empty field
            // preserves the existing ciphertext server-side.
            if (apiKey.trim()) body.api_key = apiKey.trim();

            if (editing) {
                await aiProviders.update(editing.id, body);
                toast.success("Provider updated");
            } else {
                await aiProviders.create(body);
                toast.success("Provider created");
            }
            showDialog = false;
            await load();
        } catch (e) {
            formError = errorMessage(e);
        } finally {
            saving = false;
        }
    }

    async function del(p: AIProvider) {
        if (
            !(await confirm({
                title: `Delete provider "${p.name}"?`,
                message:
                    "Agents bound to it will lose their connection until rebound.",
                tone: "danger",
                confirmLabel: "Delete",
            }))
        )
            return;
        try {
            await aiProviders.delete(p.id);
            toast.success("Provider deleted");
            await load();
        } catch (e) {
            toast.fromError(e, "Delete failed");
        }
    }

    async function test(p: AIProvider) {
        testing = { ...testing, [p.id]: "running" };
        try {
            const res = await aiProviders.test(p.id);
            testing = { ...testing, [p.id]: res.success ? "ok" : "failed" };
            if (res.success) {
                toast.success("Connection OK", {
                    description: `${res.model}: ${(res.response ?? "").slice(0, 80)}`,
                });
            } else {
                toast.error("Connection failed", { description: res.response });
            }
        } catch (e) {
            testing = { ...testing, [p.id]: "failed" };
            toast.fromError(e, "Test failed");
        }
    }

    // Default model hints per provider type — just starter values, the
    // admin is free to override.
    const MODEL_HINT: Record<string, string> = {
        openai: "gpt-5.5",
        anthropic: "claude-opus-4-7",
        gemini: "gemini-3.5-flash",
        deepseek: "deepseek-chat",
        kimi: "kimi-k2-0905-preview",
        ollama: "llama3.1",
        custom: "",
    };

    // What the backend calls when Base URL is left blank, shown as the field's
    // placeholder so a provider can be filled in without leaving the page.
    // `custom` has none — it is the one type whose base URL the backend requires.
    const BASE_URL_HINT: Record<string, string> = {
        openai: "https://api.openai.com",
        anthropic: "https://api.anthropic.com",
        gemini: "https://generativelanguage.googleapis.com",
        deepseek: "https://api.deepseek.com",
        kimi: "https://api.moonshot.ai",
        ollama: "http://host.docker.internal:11434",
        custom: "https://my-gateway.internal",
    };

    // Ollama is reached over the network from the container, so its base URL is
    // required in practice; `custom` has no vendor endpoint at all.
    const baseUrlRequired = $derived(type === "ollama" || type === "custom");

    function onTypeChange() {
        if (!editing) defaultModel = MODEL_HINT[type] ?? defaultModel;
    }
</script>

<svelte:head><title>AI Providers · Flow Weaver</title></svelte:head>

<div class="p-6 max-w-7xl mx-auto space-y-5">
    <PageHeader
        title="AI Providers"
        description="Connect FlowWeaver to an LLM backend. Keys are encrypted server-side and never returned to the UI."
        breadcrumbs={[{ label: "AI", href: "/ai" }, { label: "Providers" }]}
    >
        {#snippet actions()}
            <Button variant="ghost" icon={RefreshCw} onclick={load}
                >Refresh</Button
            >
            <Button variant="primary" icon={Plus} onclick={openCreate}
                >New provider</Button
            >
        {/snippet}
    </PageHeader>

    {#if loading}
        <div class="py-12 flex justify-center"><Spinner size="lg" /></div>
    {:else if loadError}
        <Alert tone="error">{loadError}</Alert>
    {:else if rows.length === 0}
        <Card padding="none">
            <EmptyState
                icon={Plug}
                title="No providers configured"
                description="Add an OpenAI key (gpt-5.5), an Anthropic key, a Google Gemini key, or point at a local Ollama. The default assistant auto-seeds on the next boot once a provider is enabled."
            >
                {#snippet actions()}
                    <Button variant="primary" icon={Plus} onclick={openCreate}
                        >Add provider</Button
                    >
                {/snippet}
            </EmptyState>
        </Card>
    {:else}
        <Card padding="none">
            <table class="w-full text-sm">
                <thead class="border-b border-surface-200-800">
                    <tr
                        class="text-left text-[11px] uppercase tracking-wide text-surface-500"
                    >
                        <th class="px-4 py-2">Name</th>
                        <th class="px-4 py-2">Type</th>
                        <th class="px-4 py-2">Default model</th>
                        <th class="px-4 py-2">Enabled</th>
                        <th class="px-4 py-2">Created</th>
                        <th class="px-4 py-2 text-right">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {#each rows as p (p.id)}
                        <tr
                            class="border-b border-surface-200-800/50 hover:bg-surface-100-900/40"
                        >
                            <td class="px-4 py-2 text-surface-900-100"
                                >{p.name}</td
                            >
                            <td class="px-4 py-2"
                                ><StatusBadge
                                    status="info"
                                    label={p.type}
                                    showDot={false}
                                /></td
                            >
                            <td
                                class="px-4 py-2 font-mono text-xs text-surface-700-300"
                                >{p.default_model}</td
                            >
                            <td class="px-4 py-2">
                                {#if p.enabled}
                                    <StatusBadge
                                        status="success"
                                        label="enabled"
                                        showDot={false}
                                    />
                                {:else}
                                    <StatusBadge
                                        status="pending"
                                        label="disabled"
                                        showDot={false}
                                    />
                                {/if}
                            </td>
                            <td
                                class="px-4 py-2 text-surface-500 text-[11px] tabular-nums whitespace-nowrap"
                                >{formatDateTime(p.created_at)}</td
                            >
                            <td class="px-4 py-2">
                                <div
                                    class="flex items-center justify-end gap-1"
                                >
                                    <Button
                                        size="xs"
                                        variant="ghost"
                                        onclick={() => test(p)}
                                        disabled={testing[p.id] === "running"}
                                    >
                                        {#if testing[p.id] === "running"}
                                            <Loader2
                                                size={12}
                                                class="animate-spin"
                                            /> Testing
                                        {:else if testing[p.id] === "ok"}
                                            <CheckCircle2
                                                size={12}
                                                class="text-success-300"
                                            /> OK
                                        {:else if testing[p.id] === "failed"}
                                            <XCircle
                                                size={12}
                                                class="text-error-300"
                                            /> Failed
                                        {:else}
                                            Test
                                        {/if}
                                    </Button>
                                    <Button
                                        size="xs"
                                        variant="ghost"
                                        icon={Pencil}
                                        onclick={() => openEdit(p)}>Edit</Button
                                    >
                                    <Button
                                        size="xs"
                                        variant="ghost"
                                        icon={Trash2}
                                        onclick={() => del(p)}>Delete</Button
                                    >
                                </div>
                            </td>
                        </tr>
                    {/each}
                </tbody>
            </table>
        </Card>
    {/if}
</div>

<Dialog
    bind:open={showDialog}
    title={editing ? "Edit provider" : "New provider"}
    size="md"
>
    <div class="space-y-4 p-1">
        {#if formError}
            <Alert tone="error">{formError}</Alert>
        {/if}

        <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Input
                label="Name"
                help="providers.name"
                bind:value={name}
                disabled={saving}
                placeholder="OpenAI"
            />
            <Select
                label="Type"
                help="providers.type"
                bind:value={type}
                onchange={onTypeChange}
                disabled={saving || !!editing}
            >
                <option value="openai">OpenAI</option>
                <option value="anthropic">Anthropic</option>
                <option value="gemini">Google Gemini</option>
                <option value="deepseek">DeepSeek</option>
                <option value="kimi">Kimi (Moonshot)</option>
                <option value="ollama">Ollama</option>
                <option value="custom">Custom (OpenAI-compatible)</option>
            </Select>
        </div>

        <Input
            label="Default model"
            help="providers.default_model"
            bind:value={defaultModel}
            disabled={saving}
            placeholder="gpt-5.5"
        />

        <div class="grid grid-cols-2 gap-3">
            <Input
                label="Context window (tokens, optional)"
                help="providers.context_window"
                type="number"
                min="2048"
                step="1"
                bind:value={contextWindow}
                disabled={saving}
                placeholder={type === "ollama" ? "e.g. 32768 — set this" : "vendor default"}
            />
            <Input
                label="Max output tokens (optional)"
                help="providers.max_output_tokens"
                type="number"
                min="256"
                step="1"
                bind:value={maxOutputTokens}
                disabled={saving}
                placeholder={type === "openai" ? "model maximum" : "8192"}
            />
        </div>

        <Input
            label={baseUrlRequired ? "Base URL (required)" : "Base URL (optional)"}
            help="providers.base_url"
            bind:value={baseUrl}
            disabled={saving}
            placeholder={BASE_URL_HINT[type] ?? "Override the default endpoint"}
        />

        <div>
            <Input
                label={editing
                    ? "New API key (leave empty to keep current)"
                    : "API key"}
                help="providers.api_key"
                type="password"
                revealable
                bind:value={apiKey}
                disabled={saving}
                placeholder={type === "openai"
                    ? "sk-..."
                    : type === "anthropic"
                      ? "sk-ant-..."
                      : type === "gemini"
                        ? "AIza..."
                        : type === "deepseek" || type === "kimi"
                          ? "sk-..."
                          : type === "custom"
                            ? "(only if the endpoint needs one)"
                            : "(not required for Ollama)"}
            />
            <p class="text-[11px] text-surface-500 mt-1">
                Encrypted at rest via the key-ring. Cannot be retrieved
                after save — rotate to replace.
            </p>
        </div>

        {#if type === "anthropic"}
            <Input
                label="Workspace ID (optional)"
                help="providers.workspace_id"
                bind:value={workspaceId}
                disabled={saving}
                placeholder="wrkspc_…"
            />
        {/if}

        <label class="inline-flex items-center gap-2 text-sm" for="provider-enabled">
            <FieldHint id="providers.enabled" />
            <input
                id="provider-enabled"
                type="checkbox"
                bind:checked={enabled}
                disabled={saving}
                class="accent-primary-500"
            />
            Enabled
        </label>
    </div>

    {#snippet footer()}
        <Button variant="ghost" onclick={closeDialog} disabled={saving}
            >Cancel</Button
        >
        <Button variant="primary" onclick={save} loading={saving}
            >{editing ? "Save changes" : "Create"}</Button
        >
    {/snippet}
</Dialog>
