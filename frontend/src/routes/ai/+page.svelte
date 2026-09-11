<script lang="ts">
  import { onMount } from 'svelte';
  import {
    aiConversations, aiProviders, aiAgents,
    type AiConversationSummary,
  } from '$lib/api/client';
  import {
    PageHeader, Card, Button, StatCard, Spinner,
    formatRelative,
  } from '$lib/components/ui';
  import {
    Bot, MessageSquare, Plug, Wrench, BookOpen, FileCode2, KeyRound,
    ArrowUpRight, Plus, MessageCircle, Sparkles,
  } from 'lucide-svelte';

  // The five most recent, for the list below. The stat card must NOT count
  // these — it would report the page size, not the history.
  let conversations = $state<AiConversationSummary[]>([]);
  let conversationCount = $state(0);
  let providerCount = $state(0);
  let agentCount = $state(0);
  let loading = $state(true);

  onMount(async () => {
    try {
      // All three endpoints return the ListResponse envelope
      // `{data, total, limit, offset}`. Pulling `total` keeps the count
      // accurate even when we request a minimal page size.
      const [convs, provs, agts] = await Promise.all([
        aiConversations.list(5, 0).catch(() => ({ data: [] as AiConversationSummary[], total: 0 })),
        aiProviders.list(1, 0).catch(() => ({ data: [], total: 0, limit: 1, offset: 0 })),
        aiAgents.list(1, 0).catch(() => ({ data: [], total: 0, limit: 1, offset: 0 })),
      ]);
      conversations = convs.data;
      conversationCount = convs.total ?? convs.data.length ?? 0;
      providerCount = provs.total ?? provs.data.length ?? 0;
      agentCount = agts.total ?? agts.data.length ?? 0;
    } finally {
      loading = false;
    }
  });
</script>

<svelte:head><title>AI · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-6xl mx-auto space-y-6">
  <PageHeader
    title="AI"
    description="Agent workspace: chat with the assistant, manage providers, agents, skills, and API specs."
  >
    {#snippet actions()}
      <Button variant="primary" icon={Sparkles} href="/ai/chat">Open chat</Button>
    {/snippet}
  </PageHeader>

  <section class="grid grid-cols-1 md:grid-cols-3 gap-3">
    <StatCard label="Conversations" value={conversationCount} icon={MessageSquare} tone="primary" href="/ai/chat" />
    <StatCard label="Providers" value={providerCount} icon={Plug} tone="success" href="/ai/providers" />
    <StatCard label="Agents" value={agentCount} icon={Bot} tone="warning" href="/ai/agents" />
  </section>

  <section class="grid grid-cols-1 lg:grid-cols-3 gap-4">
    <Card>
      <div class="p-4 space-y-3">
        <div class="flex items-center justify-between">
          <div class="text-xs font-semibold uppercase tracking-wide text-surface-500">Recent conversations</div>
          <Button size="xs" variant="ghost" icon={Plus} href="/ai/chat">New</Button>
        </div>
        {#if loading}
          <div class="py-6 flex justify-center"><Spinner size="sm" /></div>
        {:else if conversations.length === 0}
          <div class="text-xs text-surface-500 py-4 text-center">
            No conversations yet. Start chatting and they'll show up here.
          </div>
        {:else}
          <ul class="space-y-1">
            {#each conversations as c (c.conversation_id)}
              <li>
                <a
                  href="/ai/chat?conversation={c.conversation_id}"
                  class="block rounded-md px-2 py-2 text-sm hover:bg-surface-200-800/40 transition-colors"
                >
                  <div class="truncate text-surface-900-100">{c.title}</div>
                  <div class="text-[11px] text-surface-500 mt-0.5 flex items-center gap-2">
                    <MessageCircle size={10} /> {c.message_count} · {formatRelative(c.updated_at)}
                  </div>
                </a>
              </li>
            {/each}
          </ul>
        {/if}
      </div>
    </Card>

    <Card>
      <div class="p-4 space-y-3">
        <div class="text-xs font-semibold uppercase tracking-wide text-surface-500">Agent composition</div>
        <p class="text-xs text-surface-500">
          The default assistant combines your prompt skills with every tool below.
          Edit them anytime — the agent picks up changes on the next message.
        </p>
        <div class="space-y-2">
          <a href="/ai/skills" class="flex items-center justify-between rounded-md px-2 py-2 hover:bg-surface-200-800/40 transition-colors group">
            <div class="flex items-center gap-2 text-sm text-surface-900-100">
              <BookOpen size={14} class="text-primary-300" />
              Prompt skills
            </div>
            <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300" />
          </a>
          <a href="/ai/specs" class="flex items-center justify-between rounded-md px-2 py-2 hover:bg-surface-200-800/40 transition-colors group">
            <div class="flex items-center gap-2 text-sm text-surface-900-100">
              <FileCode2 size={14} class="text-primary-300" />
              API specs
            </div>
            <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300" />
          </a>
          <a href="/admin/secrets" class="flex items-center justify-between rounded-md px-2 py-2 hover:bg-surface-200-800/40 transition-colors group">
            <div class="flex items-center gap-2 text-sm text-surface-900-100">
              <KeyRound size={14} class="text-primary-300" />
              Secrets (admin)
            </div>
            <ArrowUpRight size={12} class="text-surface-500 group-hover:text-primary-300" />
          </a>
        </div>
      </div>
    </Card>

    <Card>
      <div class="p-4 space-y-3">
        <div class="text-xs font-semibold uppercase tracking-wide text-surface-500">Built-in tools</div>
        <p class="text-xs text-surface-500">
          The agent can call these out-of-the-box. Permission level is enforced per caller role.
        </p>
        <ul class="text-xs text-surface-700-300 space-y-1 font-mono">
          <li><Wrench size={10} class="inline text-surface-500 mr-1" /> list_apis · discover_operations</li>
          <li><Wrench size={10} class="inline text-surface-500 mr-1" /> operation_detail · execute_operation</li>
          <li><Wrench size={10} class="inline text-surface-500 mr-1" /> get_run_details · get_step_logs</li>
          <li><Wrench size={10} class="inline text-surface-500 mr-1" /> get_workflow_details · update_workflow_node_config</li>
          <li><Wrench size={10} class="inline text-surface-500 mr-1" /> list_workflows · list_services · query_devices</li>
          <li><Wrench size={10} class="inline text-surface-500 mr-1" /> create_workflow_plan</li>
        </ul>
      </div>
    </Card>
  </section>
</div>
