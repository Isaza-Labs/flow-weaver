<script lang="ts">
  // Full chat UI for the default assistant agent. Streams
  // responses via /api/ai/chat/stream — text deltas render character-by-
  // character and tool calls show up as collapsible cards. Conversation
  // history lives in the left sidebar; selecting one loads its messages
  // and continues the thread.
  //
  // Entry points that hand us rich context:
  //   ?fix=<json>      — "Fix with AI" / "Edit with AI" from runs monitor
  //   ?context=<slug>  — mascot "Open full chat" from any guide route
  //   ?q=<text>        — preload the input with a prompt
  //
  // When `fix` is present we auto-send after a short grace period so the
  // user lands on a conversation already in progress.

  import { onMount, onDestroy, tick } from 'svelte';
  import { page } from '$app/state';
  import {
    aiConversations, aiAgents,
    type AiConversationSummary, type AiConversationDetail,
    errorMessage,
  } from '$lib/api/client';
  import { streamChat, type ChatStreamEvent } from '$lib/api/ai-stream';
  import ChatMarkdown from '$lib/components/ChatMarkdown.svelte';
  import ChatToolCalls from '$lib/components/ChatToolCalls.svelte';
  import ChatThinkingDots from '$lib/components/ChatThinkingDots.svelte';
  import AgentPicker from '$lib/components/AgentPicker.svelte';
  import {
    Button, IconButton, Textarea, Spinner, Alert, EmptyState,
    toast, formatRelative, confirm,
  } from '$lib/components/ui';
  import { authStore } from '$lib/stores/auth.svelte';
  import {
    Send, Bot, User, Plus, Trash2, Info,
    MessageCircle, AlertTriangle, Clock, ArrowDown,
  } from 'lucide-svelte';

  const isAdmin = $derived(authStore.session?.role === 'admin');

  // ─── state ───────────────────────────────────────────────────────────

  interface ToolCall {
    name: string;
    status: 'running' | 'ok' | 'failed';
    args_preview?: unknown;
    preview?: unknown;
    attachment?: import('$lib/api/ai-stream').ToolAttachment;
  }

  interface ChatMessage {
    id: string;
    role: 'user' | 'assistant' | 'system';
    content: string;
    pending?: boolean;
    toolCalls?: ToolCall[];
    tokens?: { in?: number; out?: number };
    timeout?: boolean;
    timeoutDeadlineS?: number;
    error?: string;
    errorCode?: string;
  }

  // The sidebar shows one page. `conversationTotal` is the caller's whole
  // history, so a user with more than CONVERSATION_PAGE_SIZE of them is told
  // the list is truncated instead of quietly seeing a prefix.
  const CONVERSATION_PAGE_SIZE = 50;
  let conversations = $state<AiConversationSummary[]>([]);
  let conversationTotal = $state(0);
  let conversationId = $state<string | null>(null);
  let messages = $state<ChatMessage[]>([]);
  let input = $state('');
  let loadingList = $state(true);
  let loadingThread = $state(false);
  let streaming = $state(false);
  let abortCtrl: AbortController | null = null;
  let threadEl = $state<HTMLDivElement | undefined>(undefined);
  // The prompt box. Held so we can put focus back after a send — see
  // refocusInput().
  let inputEl = $state<HTMLTextAreaElement | null>(null);
  // When the user scrolls up mid-stream we stop yanking them to the bottom
  // and surface a "Jump to latest" affordance instead.
  let showJumpToLatest = $state(false);
  let agents = $state<Array<{ id: string; name: string }>>([]);
  let agentId = $state<string>('');
  let fixContext = $state<Record<string, unknown> | null>(null);
  let inlineError = $state<string | null>(null);

  // Uniquely identify messages without pulling in a uuid dep — the id is
  // only used as a keyed-each key inside this component.
  let msgCounter = 0;
  const nextId = () => `m-${++msgCounter}-${Date.now()}`;

  // ─── lifecycle ───────────────────────────────────────────────────────

  onMount(async () => {
    await Promise.all([loadConversations(), loadAgents()]);
    await applyQueryParams();
  });

  onDestroy(() => {
    abortCtrl?.abort();
  });

  async function loadConversations() {
    loadingList = true;
    try {
      const res = await aiConversations.list(CONVERSATION_PAGE_SIZE, 0);
      conversations = res.data;
      conversationTotal = res.total;
    } catch (e) {
      toast.fromError(e, "Couldn't load conversations");
    } finally {
      loadingList = false;
    }
  }

  async function loadAgents() {
    try {
      const list = await aiAgents.list();
      // aiAgents.list() returns the paginated envelope { data, total, ... };
      // read .data. Tolerate a bare array too in case the shape ever changes.
      const arr = Array.isArray(list) ? list : (list?.data ?? []);
      agents = arr
        .filter((a): a is { id: string; name: string } =>
          a != null && typeof a === 'object' && 'id' in a && 'name' in a)
        .map((a) => ({ id: String(a.id), name: String(a.name) }));
    } catch {
      agents = [];
    }
  }

  // Consumes ?conversation / ?fix / ?context / ?q when the page mounts.
  // Each one is mutually compatible — e.g. "Fix with AI" can drop fixContext
  // on top of a preselected agent.
  async function applyQueryParams() {
    const params = page.url.searchParams;
    const existing = params.get('conversation');
    if (existing) {
      await openConversation(existing);
      // A reload lands on an already-loaded thread; jump to the newest
      // message so the user resumes where they left off, not at the top.
      await scrollToBottom();
    }

    const fix = params.get('fix');
    if (fix) {
      try {
        fixContext = JSON.parse(fix) as Record<string, unknown>;
        input = buildFixPrompt(fixContext);
      } catch {
        inlineError = 'Invalid ?fix parameter — expected JSON';
      }
    }

    const context = params.get('context');
    if (context && !input) input = `Help me with: ${context}`;

    const q = params.get('q');
    if (q) input = q;

    // Auto-send only for the fix flow — other entry points let the user
    // edit the prompt first. We await a tick so the textarea's $state
    // binding is flushed before send() reads `input`, then fire
    // immediately; the old 300 ms timeout occasionally lost the message
    // because a concurrent goto/replaceState would clear the pending
    // closure before it ran.
    if (fixContext && input && !streaming) {
      await tick();
      if (input && !streaming) send();
    }
  }

  function buildFixPrompt(ctx: Record<string, unknown>): string {
    if (ctx.mode === 'edit' && ctx.node_id) {
      return `Edit node \`${ctx.node_id}\` in workflow ${ctx.workflow_id}. `
        + `Describe the change you need — the agent can inspect the current DAG `
        + `and apply the new config directly via update_workflow_node_config.`;
    }
    const parts: string[] = [];
    parts.push(`The following step failed in run ${ctx.run_id}:`);
    if (ctx.node_id) parts.push(`- Node: ${ctx.node_id}`);
    if (ctx.service_id) parts.push(`- Service id: ${ctx.service_id}`);
    if (ctx.error) parts.push(`- Error: ${ctx.error}`);
    if (ctx.workflow_id) parts.push(`- Workflow: ${ctx.workflow_id}`);
    parts.push('');
    parts.push('Diagnose the failure (use get_run_details, get_step_logs, get_workflow_details), '
      + 'explain the root cause, and apply a fix to the workflow if one is clear.');
    return parts.join('\n');
  }

  // ─── send flow ───────────────────────────────────────────────────────

  async function send() {
    const text = input.trim();
    if (!text || streaming) return;

    inlineError = null;
    input = '';

    const userMsg: ChatMessage = { id: nextId(), role: 'user', content: text };
    const assistantId = nextId();
    const assistant: ChatMessage = {
      id: assistantId,
      role: 'assistant',
      content: '',
      pending: true,
      toolCalls: [],
    };
    messages = [...messages, userMsg, assistant];
    await scrollToBottom();

    streaming = true;
    abortCtrl = new AbortController();

    // Svelte 5 deep-proxies $state arrays when items are read via indexed
    // access, but mutations on captured local references bypass the
    // proxy. `updateAssistant` always rebuilds the messages array with a
    // fresh assistant object, so reactivity fires on every event.
    const updateAssistant = (mutate: (prev: ChatMessage) => ChatMessage) => {
      const idx = messages.findIndex((m) => m.id === assistantId);
      if (idx < 0) return;
      const next = mutate(messages[idx]);
      messages = [...messages.slice(0, idx), next, ...messages.slice(idx + 1)];
    };

    try {
      for await (const evt of streamChat(
        {
          message: text,
          agent_id: agentId || undefined,
          conversation_id: conversationId ?? undefined,
        },
        abortCtrl.signal,
      )) {
        if (evt.type === 'conversation') {
          // The backend always emits this as the first event now. Capture
          // the id so follow-up messages land in the same thread instead
          // of spawning a new conversation on every send.
          conversationId = evt.id;
          continue;
        }
        updateAssistant((prev) => applyEvent(prev, evt));
        await maybeAutoScroll();
      }
    } catch (e) {
      const message = errorMessage(e);
      updateAssistant((prev) => ({ ...prev, error: message }));
    } finally {
      updateAssistant((prev) => ({ ...prev, pending: false }));
      streaming = false;
      abortCtrl = null;
      await refocusInput();
      // Refresh sidebar so a brand-new thread shows up without a manual
      // reload. We delay a tick so the server has time to commit.
      setTimeout(loadConversations, 500);
    }
  }

  // The prompt box is `disabled` while streaming, and the browser drops
  // focus from an element the moment it becomes disabled — so after every
  // turn the caret was gone and the user had to click the box again. Put
  // focus back once the box is editable again.
  //
  // Skipped when focus has deliberately moved somewhere else (another
  // field, the sidebar): a disabled element leaves focus on <body>, so
  // "nothing else is focused" is the signal that we're only restoring what
  // the disable took away, not stealing it. `force` is for the paths where
  // the user's own click asked for the prompt box (New conversation) — the
  // button itself holds focus there, so the guard would skip it.
  async function refocusInput(force = false) {
    await tick();
    if (!force) {
      const active = document.activeElement;
      if (active && active !== document.body && active !== inputEl) return;
    }
    inputEl?.focus();
  }

  // Pure reducer: returns a new ChatMessage with the event applied. Any
  // mutation here would miss Svelte's proxy and leave the UI stale.
  function applyEvent(msg: ChatMessage, evt: ChatStreamEvent): ChatMessage {
    switch (evt.type) {
      case 'text':
        return { ...msg, content: msg.content + evt.content };
      case 'tool_start':
        return {
          ...msg,
          toolCalls: [
            ...(msg.toolCalls ?? []),
            { name: evt.name, status: 'running', args_preview: evt.args_preview },
          ],
        };
      case 'tool_result': {
        const calls = (msg.toolCalls ?? []).slice();
        for (let i = calls.length - 1; i >= 0; i--) {
          if (calls[i].name === evt.name && calls[i].status === 'running') {
            calls[i] = {
              ...calls[i],
              status: evt.success ? 'ok' : 'failed',
              preview: evt.preview,
              attachment: evt.attachment,
            };
            break;
          }
        }
        return { ...msg, toolCalls: calls };
      }
      case 'done':
        return { ...msg, tokens: { in: evt.tokens_in, out: evt.tokens_out } };
      case 'timeout':
        return {
          ...msg,
          timeout: true,
          timeoutDeadlineS: evt.deadline_s,
          content: evt.partial
            ? msg.content + `\n\n(stream timed out${evt.deadline_s ? ` at ${fmtDeadline(evt.deadline_s)}` : ''})`
            : msg.content,
        };
      case 'error':
        return { ...msg, error: evt.message, errorCode: evt.code };
      default:
        return msg;
    }
  }

  function cancelStream() {
    abortCtrl?.abort();
    toast.info('Stopped generation');
  }

  // Human label for the stream deadline the backend reports in a `timeout`
  // event, e.g. 120 → "2 min", 90 → "90s". Keeps the timeout notice in sync
  // with AiChat__StreamDeadlineSeconds instead of a hardcoded number.
  function fmtDeadline(seconds: number): string {
    return seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds}s`;
  }

  // Treat the user as "following" the stream while they sit within 80px of
  // the bottom. Past that they've scrolled up to read, so we leave them be.
  const NEAR_BOTTOM_PX = 80;
  function isNearBottom(): boolean {
    if (!threadEl) return true;
    return threadEl.scrollHeight - threadEl.scrollTop - threadEl.clientHeight < NEAR_BOTTOM_PX;
  }

  // Unconditional scroll for the cases where we always want the user at the
  // latest message: just sent a prompt, opened a conversation, or tapped
  // "Jump to latest".
  async function scrollToBottom() {
    await tick();
    if (threadEl) threadEl.scrollTop = threadEl.scrollHeight;
    showJumpToLatest = false;
  }

  // Keep the jump-to-latest button in sync with scroll position outside of
  // streaming too: a reload lands you at the top of a long thread, and
  // scrolling up to read history should surface the button so one tap
  // returns to the newest message.
  function onThreadScroll() {
    showJumpToLatest = messages.length > 0 && !isNearBottom();
  }

  // Called after every stream delta. Only pins to the bottom when the user
  // is still following along; otherwise reveal the jump affordance so we
  // don't fight their scroll position.
  async function maybeAutoScroll() {
    await tick();
    if (isNearBottom()) {
      if (threadEl) threadEl.scrollTop = threadEl.scrollHeight;
      showJumpToLatest = false;
    } else {
      showJumpToLatest = true;
    }
  }

  // ─── conversations sidebar ───────────────────────────────────────────

  async function openConversation(id: string) {
    if (id === conversationId) return;
    loadingThread = true;
    try {
      const detail = (await aiConversations.get(id)) as AiConversationDetail;
      conversationId = detail.conversation_id;
      messages = hydrateMessages(detail);
      await scrollToBottom();
    } catch (e) {
      toast.fromError(e, "Couldn't open conversation");
    } finally {
      loadingThread = false;
    }
  }

  // Server stores conversations as {role, content} pairs. We map each
  // into a ChatMessage without a server-assigned id — new ones we append
  // mid-session carry our own ids.
  function hydrateMessages(detail: AiConversationDetail): ChatMessage[] {
    const raw = detail.messages as unknown;
    if (!Array.isArray(raw)) return [];
    return raw
      .filter((m): m is { role: string; content: string } =>
        m != null && typeof m === 'object' && 'role' in m && 'content' in m)
      .filter((m) => m.role !== 'system')
      .map((m) => ({
        id: nextId(),
        role: (m.role === 'assistant' ? 'assistant' : 'user') as ChatMessage['role'],
        content: typeof m.content === 'string' ? m.content : '',
      }));
  }

  function startNewConversation() {
    conversationId = null;
    messages = [];
    input = '';
    fixContext = null;
    inlineError = null;
    // Land the caret in the empty prompt box — starting a new thread is
    // always followed by typing.
    void refocusInput(true);
  }

  async function deleteAllConversations() {
    if (!isAdmin) return;
    const count = conversations.length;
    if (count === 0) return;
    const ok = await confirm({
      title: 'Delete all conversations',
      message: `All ${count} conversation${count === 1 ? '' : 's'} will be permanently removed. This affects every user.`,
      confirmLabel: 'Delete all',
      cancelLabel: 'Cancel',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      const res = await aiConversations.deleteAll();
      startNewConversation();
      await loadConversations();
      toast.success(`Deleted ${res.deleted} conversation${res.deleted === 1 ? '' : 's'}`);
    } catch (e) {
      toast.fromError(e, 'Delete all failed');
    }
  }

  async function deleteConversation(id: string) {
    const target = conversations.find((c) => c.conversation_id === id);
    const ok = await confirm({
      title: 'Delete conversation',
      message: target?.title
        ? `"${target.title}" and its history will be permanently removed.`
        : 'This conversation and its history will be permanently removed.',
      confirmLabel: 'Delete',
      cancelLabel: 'Cancel',
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await aiConversations.delete(id);
      if (id === conversationId) startNewConversation();
      await loadConversations();
      toast.success('Conversation deleted');
    } catch (e) {
      toast.fromError(e, 'Delete failed');
    }
  }

  // ─── slash commands ──────────────────────────────────────────────────
  // Convenience shortcuts that rewrite into plain prompts when the user
  // hits space after the command:
  //   /run <id>      → "Analyze run <id>."
  //   /workflow <id> → "Describe workflow <id>."
  //   /device <ip>   → "Show info for device <ip>."

  function onInputKey(ev: KeyboardEvent) {
    // Don't submit mid-IME composition — Enter there commits the candidate
    // rather than the message.
    if (ev.isComposing) return;

    if (ev.key === 'Enter' && !ev.shiftKey) {
      ev.preventDefault();
      send();
      return;
    }

    if (ev.key === ' ') {
      const m = input.match(/^\/(run|workflow|device)\s*$/);
      if (m) {
        ev.preventDefault();
        input = `${expandCommand(m[1])} `;
      }
    }
  }

  function expandCommand(cmd: string): string {
    switch (cmd) {
      case 'run': return 'Analyze run';
      case 'workflow': return 'Describe workflow';
      case 'device': return 'Show info for device';
      default: return `/${cmd}`;
    }
  }
</script>

<svelte:head><title>AI Chat · FlowWeaver</title></svelte:head>

<div class="flex h-[calc(100vh-3.5rem)] bg-surface-50-950">
  <!-- Sidebar -->
  <aside class="w-72 shrink-0 border-r border-surface-200-800 flex flex-col bg-surface-100-900/40">
    <div class="flex items-center justify-between px-4 h-12 border-b border-surface-200-800">
      <div class="flex items-center gap-2">
        <div class="w-6 h-6 rounded-md bg-primary-500/15 text-primary-300 flex items-center justify-center ring-1 ring-primary-500/30">
          <Bot size={14} />
        </div>
        <div class="text-sm font-semibold text-surface-900-100">Conversations</div>
      </div>
      <Button size="xs" variant="ghost" icon={Plus} onclick={startNewConversation}>New</Button>
    </div>

    {#if isAdmin && conversations.length > 0}
      <div class="px-3 py-2 border-b border-surface-200-800 flex justify-end">
        <Button
          size="xs"
          variant="ghost"
          icon={Trash2}
          onclick={deleteAllConversations}
          title="Delete every conversation (admin only)"
        >
          Clear all
        </Button>
      </div>
    {/if}

    <div class="flex-1 overflow-y-auto">
      {#if loadingList}
        <div class="py-8 flex justify-center"><Spinner size="sm" /></div>
      {:else if conversations.length === 0}
        <div class="px-4 py-6 text-xs text-surface-500 text-center">
          No conversations yet. Start chatting and the thread will appear here.
        </div>
      {:else}
        {#each conversations as c (c.conversation_id)}
          <!-- Open and delete are sibling controls — keeping the delete
               button nested inside the open button would be invalid nested
               interactive content. -->
          <div
            class="flex items-start gap-1 px-3 py-2 border-b border-surface-200-800/50 hover:bg-surface-200-800/30 transition-colors group {c.conversation_id === conversationId ? 'bg-surface-200-800/50' : ''}"
          >
            <button
              type="button"
              onclick={() => openConversation(c.conversation_id)}
              class="flex-1 min-w-0 text-left"
            >
              <div class="text-sm text-surface-900-100 truncate">{c.title}</div>
              <div class="text-[11px] text-surface-500 mt-0.5 flex items-center gap-2">
                <MessageCircle size={10} />
                <span>{c.message_count}</span>
                <span>·</span>
                <span>{formatRelative(c.updated_at)}</span>
              </div>
            </button>
            <div class="shrink-0 opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity">
              <IconButton
                icon={Trash2}
                label="Delete conversation"
                size="xs"
                variant="danger"
                onclick={() => deleteConversation(c.conversation_id)}
              />
            </div>
          </div>
        {/each}
        {#if conversationTotal > conversations.length}
          <div class="px-3 py-2 text-[11px] text-surface-500 text-center">
            Showing the {conversations.length} most recent of {conversationTotal}.
          </div>
        {/if}
      {/if}
    </div>
  </aside>

  <!-- Main thread -->
  <section class="relative flex-1 flex flex-col min-w-0">
    <div class="flex items-center justify-between px-4 h-12 border-b border-surface-200-800 bg-surface-100-900/40">
      <div class="min-w-0">
        <div class="text-sm font-semibold text-surface-900-100">
          {conversationId ? 'Continue conversation' : 'New conversation'}
        </div>
        {#if fixContext}
          <div class="text-[11px] text-warning-300 flex items-center gap-1.5 mt-0.5">
            <AlertTriangle size={10} />
            Context attached from {(fixContext.mode === 'edit') ? 'Edit with AI' : 'Fix with AI'}
          </div>
        {/if}
      </div>
      <Button size="xs" variant="ghost" href="/ai">Back to /ai</Button>
    </div>

    <div bind:this={threadEl} onscroll={onThreadScroll} class="flex-1 overflow-y-auto px-4 py-4 space-y-4">
      {#if loadingThread}
        <div class="py-10 flex justify-center"><Spinner size="lg" /></div>
      {:else if messages.length === 0}
        <EmptyState
          icon={Bot}
          title="Ask the agent anything"
          description={'It has access to your workflows, runs, devices, API specs, and secrets. '
            + 'Try "list apis" or "diagnose run <id>".'}
        />
      {:else}
        {#each messages as m (m.id)}
          <div class="flex gap-3 {m.role === 'user' ? 'justify-end' : 'justify-start'}">
            {#if m.role === 'assistant'}
              <div class="w-7 h-7 rounded-full bg-primary-500/15 text-primary-300 flex items-center justify-center shrink-0 ring-1 ring-primary-500/30">
                <Bot size={14} />
              </div>
            {/if}
            <div class="max-w-[min(720px,85%)] space-y-2 {m.role === 'user' ? 'items-end' : ''}">
              {#if m.role === 'user'}
                <div class="bg-primary-500/15 ring-1 ring-primary-500/30 text-surface-900-100 rounded-lg rounded-tr-sm px-3 py-2 text-sm whitespace-pre-wrap break-words">
                  {m.content}
                </div>
              {:else}
                {#if m.toolCalls && m.toolCalls.length > 0}
                  <ChatToolCalls calls={m.toolCalls} />
                {/if}

                {#if m.content}
                  <div class="text-sm text-surface-900-100 break-words leading-relaxed relative">
                    <ChatMarkdown content={m.content} />
                    {#if m.pending}<span class="inline-block w-1.5 h-4 bg-primary-400 animate-pulse align-text-bottom ml-0.5"></span>{/if}
                  </div>
                {:else if m.pending && !(m.toolCalls && m.toolCalls.length > 0)}
                  <ChatThinkingDots />
                {/if}

                {#if m.timeout}
                  <Alert tone="warning">
                    <div class="flex items-center gap-1.5 text-xs">
                      <Clock size={12} /> The stream hit its deadline{m.timeoutDeadlineS ? ` (${fmtDeadline(m.timeoutDeadlineS)})` : ''}. Try a narrower prompt.
                    </div>
                  </Alert>
                {/if}
                {#if m.error}
                  <Alert tone="error">
                    {#if m.errorCode === 'no_provider'}
                      <div class="space-y-2">
                        <div class="text-sm font-medium">No AI provider is configured yet.</div>
                        <div class="text-xs opacity-80">Add one under <a href="/ai/providers" class="underline text-primary-300 hover:text-primary-200">Settings &rsaquo; AI Providers</a> and try again.</div>
                      </div>
                    {:else if m.errorCode === 'no_agent'}
                      <div class="space-y-2">
                        <div class="text-sm font-medium">No AI agent is configured yet.</div>
                        <div class="text-xs opacity-80">Create an enabled <span class="font-medium">assistant</span> agent under <a href="/ai/agents" class="underline text-primary-300 hover:text-primary-200">Settings &rsaquo; AI Agents</a> and try again.</div>
                      </div>
                    {:else}
                      {m.error}
                    {/if}
                  </Alert>
                {/if}
                {#if m.tokens && ((m.tokens.in ?? 0) > 0 || (m.tokens.out ?? 0) > 0)}
                  <!-- Token telemetry: hidden by default, surfaces on hover
                       so operators can still read it without cluttering
                       the conversation for regular users. -->
                  <div
                    class="group inline-flex items-center gap-1 text-[10px] text-surface-500/60 hover:text-surface-500 transition-colors cursor-help select-none"
                    title={`${m.tokens.in ?? 0} tokens in · ${m.tokens.out ?? 0} tokens out`}
                  >
                    <Info size={10} />
                    <span class="opacity-0 group-hover:opacity-100 transition-opacity tabular-nums">
                      {m.tokens.in ?? 0} in · {m.tokens.out ?? 0} out
                    </span>
                  </div>
                {/if}
              {/if}
            </div>
            {#if m.role === 'user'}
              <div class="w-7 h-7 rounded-full bg-surface-200-800 text-surface-500 flex items-center justify-center shrink-0">
                <User size={14} />
              </div>
            {/if}
          </div>
        {/each}
      {/if}
    </div>

    {#if showJumpToLatest}
      <!-- Anchored just above the composer; shown whenever the user is
           scrolled away from the bottom — mid-stream, after a reload, or
           while reading earlier history. -->
      <div class="pointer-events-none absolute inset-x-0 bottom-24 flex justify-center">
        <button
          type="button"
          onclick={scrollToBottom}
          class="pointer-events-auto inline-flex items-center gap-1.5 rounded-full bg-surface-200-800 text-surface-700-300 ring-1 ring-surface-300-700 shadow-md px-3 py-1.5 text-xs hover:bg-surface-300-700 transition-colors"
        >
          <ArrowDown size={14} />
          Jump to latest
        </button>
      </div>
    {/if}

    {#if inlineError}
      <div class="px-4 pb-2"><Alert tone="error">{inlineError}</Alert></div>
    {/if}

    <div class="border-t border-surface-200-800 bg-surface-100-900/40 p-3">
      <!-- Next to the text box, not in the sidebar: whoever is typing needs
           to see who they are writing to without looking away. -->
      {#if agents.length > 0}
        <div class="mb-2 flex items-center">
          <AgentPicker {agents} bind:value={agentId} disabled={streaming} />
        </div>
      {/if}
      <div class="flex items-end gap-2">
        <div class="flex-1">
          <Textarea
            bind:value={input}
            bind:element={inputEl}
            placeholder="Ask the agent — Shift+Enter for a newline. Try /run <id> or /workflow <id>."
            disabled={streaming}
            rows={2}
            onkeydown={onInputKey}
          />
        </div>
        {#if streaming}
          <Button variant="danger" onclick={cancelStream}>Stop</Button>
        {:else}
          <Button variant="primary" icon={Send} onclick={send} disabled={!input.trim()}>Send</Button>
        {/if}
      </div>
    </div>
  </section>
</div>
