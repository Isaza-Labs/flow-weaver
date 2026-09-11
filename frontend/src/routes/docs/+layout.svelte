<script lang="ts">
  import type { Snippet } from 'svelte';
  import { page } from '$app/state';
  import { authStore } from '$lib/stores/auth.svelte';
  import { BookOpen, Shield } from 'lucide-svelte';

  let { children }: { children: Snippet } = $props();

  type DocLink = { href: string; label: string; adminOnly?: boolean };
  type DocGroup = { label: string; items: DocLink[]; adminOnly?: boolean };

  const groups: DocGroup[] = [
    {
      label: 'Introduction',
      items: [
        { href: '/docs', label: 'Welcome' },
        { href: '/docs/getting-started', label: 'Getting started' },
        { href: '/docs/dashboard', label: 'Dashboard' },
      ],
    },
    {
      label: 'Build',
      items: [
        { href: '/docs/workflows', label: 'Workflows' },
        { href: '/docs/snippets', label: 'Snippets' },
      ],
    },
    {
      // Mirrors the app sidebar's Integrate group: everything that connects
      // FlowWeaver to a system outside it.
      label: 'Integrate',
      items: [
        { href: '/docs/integrations', label: 'Integrations' },
        { href: '/docs/admin/mcp-servers', label: 'MCP servers', adminOnly: true },
        { href: '/docs/ai/channels', label: 'Messaging channels' },
        { href: '/docs/email', label: 'Email', adminOnly: true },
      ],
    },
    {
      label: 'Operate',
      items: [
        { href: '/docs/runs', label: 'Runs' },
        { href: '/docs/schedules', label: 'Schedules' },
        { href: '/docs/devices', label: 'Devices' },
        { href: '/docs/device-pools', label: 'Device pools' },
        { href: '/docs/credentials', label: 'Credentials' },
        { href: '/docs/qa-lab', label: 'QA lab' },
      ],
    },
    {
      label: 'Govern',
      adminOnly: true,
      items: [
        { href: '/docs/policies', label: 'Policies', adminOnly: true },
      ],
    },
    {
      label: 'Intelligence',
      items: [
        { href: '/docs/ai', label: 'AI overview' },
        { href: '/docs/ai/chat', label: 'Chat' },
        { href: '/docs/ai/providers', label: 'Providers' },
        { href: '/docs/ai/agents', label: 'Agents' },
        { href: '/docs/ai/skills', label: 'Skills' },
        { href: '/docs/ai/specs', label: 'API specs' },
      ],
    },
    {
      label: 'Administration',
      adminOnly: true,
      items: [
        { href: '/docs/admin', label: 'Admin dashboard', adminOnly: true },
        { href: '/docs/admin/users', label: 'Users', adminOnly: true },
        { href: '/docs/admin/audit', label: 'Audit log', adminOnly: true },
        { href: '/docs/admin/traces', label: 'Traces', adminOnly: true },
        { href: '/docs/admin/artifacts', label: 'Artifacts', adminOnly: true },
      ],
    },
    {
      label: 'Account',
      items: [
        { href: '/docs/account', label: 'Account & session' },
        { href: '/docs/themes', label: 'Themes' },
      ],
    },
  ];

  const isAdmin = $derived(authStore.session?.role === 'admin');

  function visible(link: DocLink): boolean {
    return !link.adminOnly || isAdmin;
  }

  function groupHasVisibleItems(group: DocGroup): boolean {
    return group.items.some(visible);
  }

  function isActive(href: string): boolean {
    const path = page.url.pathname;
    if (href === '/docs') return path === '/docs' || path === '/docs/';
    return path === href;
  }

  // Derive a page-specific title from the currently-active link so the
  // browser tab reflects what the user is reading instead of a generic
  // "Documentation".
  const docTitle = $derived.by(() => {
    for (const group of groups) {
      for (const link of group.items) {
        if (isActive(link.href)) return `${link.label} · Docs · FlowWeaver`;
      }
    }
    return 'Documentation · FlowWeaver';
  });
</script>

<svelte:head><title>{docTitle}</title></svelte:head>

<div class="flex h-full min-h-screen">
  <!-- Docs nav -->
  <aside class="w-64 shrink-0 border-r border-surface-200-800/60 bg-surface-100-900/30 overflow-y-auto">
    <div class="px-4 h-12 flex items-center gap-2 border-b border-surface-200-800/60 sticky top-0 bg-surface-100-900/80 backdrop-blur-sm z-10">
      <BookOpen size={15} class="text-primary-300" />
      <div class="text-sm font-semibold text-surface-900-100">User manual</div>
    </div>

    <nav class="px-2 py-3 space-y-4">
      {#each groups as group}
        {#if groupHasVisibleItems(group)}
          <div>
            <div class="px-2 pb-1 text-[10px] font-semibold uppercase tracking-[0.08em] text-surface-500/80 flex items-center gap-1">
              {group.label}
              {#if group.adminOnly}
                <Shield size={10} class="text-primary-400" />
              {/if}
            </div>
            <div class="flex flex-col gap-0.5">
              {#each group.items as link}
                {#if visible(link)}
                  {@const active = isActive(link.href)}
                  <a
                    href={link.href}
                    class="flex items-center justify-between gap-2 h-7 px-2 rounded text-[13px] transition-colors
                      {active
                        ? 'bg-primary-500/10 text-primary-200 ring-1 ring-inset ring-primary-500/20'
                        : 'text-surface-600-400 hover:text-surface-900-100 hover:bg-surface-200-800/40'}"
                  >
                    <span class="truncate">{link.label}</span>
                    {#if link.adminOnly}
                      <Shield size={10} class="text-primary-400/60 shrink-0" />
                    {/if}
                  </a>
                {/if}
              {/each}
            </div>
          </div>
        {/if}
      {/each}
    </nav>
  </aside>

  <!-- Content -->
  <div class="flex-1 min-w-0 overflow-y-auto">
    {@render children()}
  </div>
</div>
